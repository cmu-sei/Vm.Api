// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.


using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Player.Api.Client;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Infrastructure.HttpHandlers;
using Player.Vm.Api.Infrastructure.OperationFilters;
using Player.Vm.Api.Infrastructure.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

namespace Player.Vm.Api.Infrastructure.Extensions
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Named client for guest file transfers. Shared with VsphereService rather than repeated as a
        /// literal at both ends: a typo would resolve to an unconfigured default client - 100 second
        /// timeout, full certificate validation - with no error, which only breaks the deployments that
        /// need SkipGuestFileCertificateValidation.
        /// </summary>
        public const string GuestFileClientName = "vSphereGuestFile";

        #region Swagger

        public static void AddSwagger(this IServiceCollection services, AuthorizationOptions authOptions)
        {
            // XML Comments path
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string commentsFileName = Assembly.GetExecutingAssembly().GetName().Name + ".xml";
            string commentsFile = Path.Combine(baseDirectory, commentsFileName);

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Player VM API", Version = "v1" });

                c.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
                {
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.OAuth2,
                    Flows = new OpenApiOAuthFlows
                    {
                        AuthorizationCode = new OpenApiOAuthFlow
                        {
                            AuthorizationUrl = new Uri(authOptions.AuthorizationUrl),
                            TokenUrl = new Uri(authOptions.TokenUrl),
                            Scopes = new Dictionary<string, string>()
                            {
                                {authOptions.AuthorizationScope, "public api access"}
                            }
                        }
                    }
                });

                c.AddSecurityRequirement((document) => new OpenApiSecurityRequirement
                {
                    { new OpenApiSecuritySchemeReference("oauth2", document), [authOptions.AuthorizationScope] }
                });

                c.EnableAnnotations();
                c.IncludeXmlComments(commentsFile);
                c.CustomSchemaIds(schemaIdStrategy);
                c.OperationFilter<DefaultResponseOperationFilter>();
                c.OperationFilter<JsonIgnoreQueryOperationFilter>();
                c.OperationFilter<JsonIgnoreFormDataOperationFilter>();
                c.DocumentFilter<ModelDocumentFilter>();
            });
        }

        private static string schemaIdStrategy(Type currentClass)
        {
            var dataContractAttribute = currentClass.GetCustomAttribute<DataContractAttribute>();
            return dataContractAttribute != null && dataContractAttribute.Name != null ? dataContractAttribute.Name : currentClass.Name;
        }

        #endregion

        #region Api Clients

        public static void AddApiClients(
            this IServiceCollection services,
            IdentityClientOptions identityClientOptions,
            ClientOptions clientOptions,
            IsoUploadOptions isoUploadOptions)
        {
            services.AddHttpClient();
            services.AddIdentityClient(identityClientOptions);
            services.AddPlayerClient(clientOptions);
            services.AddDatastoreClient(isoUploadOptions);
            services.AddGuestFileClient();
            services.AddProxmoxClient();
            services.AddProxmoxIsoUploadClient(isoUploadOptions);
            services.AddTransient<AuthenticatingHandler>();
        }

        // Named HttpClient for the Corsinvest PveClient. Given none, it news up an HttpClient and
        // HttpClientHandler of its own and never disposes either; ProxmoxService is scoped, so that
        // leaks a socket pool per request and never picks up a DNS change. Both settings below are
        // applied by PveClientBase only to that internal handler, so injecting a client means
        // replicating them here or losing them silently.
        private static void AddProxmoxClient(this IServiceCollection services)
        {
            services.AddHttpClient("proxmox")
                .ConfigurePrimaryHttpMessageHandler(ProxmoxPrimaryHandler);
        }

        // Separate client for pushing ISOs to a PVE storage, because the "proxmox" client above sets no
        // Timeout and so inherits HttpClient's 100 second default - which would have to cover a whole
        // multi-gigabyte body. Raising the timeout on the shared client instead would delay failure
        // detection in the state and task pollers, which is the opposite of what they want.
        private static void AddProxmoxIsoUploadClient(
            this IServiceCollection services,
            IsoUploadOptions isoUploadOptions)
        {
            services.AddHttpClient("proxmoxIsoUpload", client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(isoUploadOptions.UploadTimeoutMinutes <= 0 ? 60 : isoUploadOptions.UploadTimeoutMinutes);
                })
                .ConfigurePrimaryHttpMessageHandler(ProxmoxPrimaryHandler);
        }

        // Shared by both Proxmox clients. PveClientBase applies these two settings only to the handler
        // it news up itself, so any client injected into it has to replicate them or lose them silently.
        private static HttpMessageHandler ProxmoxPrimaryHandler(IServiceProvider sp)
        {
            // IOptionsMonitor rather than ProxmoxOptions: the latter is registered Scoped
            // (Startup.cs), and this factory runs outside any scope.
            var proxmoxOptions = sp.GetRequiredService<IOptionsMonitor<ProxmoxOptions>>().CurrentValue;

            return new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = proxmoxOptions.ValidateCertificate
                    ? null
                    : (_, _, _, _) => true
            };
        }

        // Named HttpClient used to PUT ISOs directly to a vSphere datastore (UploadToDatastore mode).
        private static void AddDatastoreClient(
            this IServiceCollection services,
            IsoUploadOptions isoUploadOptions)
        {
            services.AddHttpClient("vSphereDatastore", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(isoUploadOptions.UploadTimeoutMinutes <= 0 ? 60 : isoUploadOptions.UploadTimeoutMinutes);
            });
        }

        // Named HttpClient for guest file transfers, which go directly to the ESXi host that runs the
        // Vm rather than through vCenter. VsphereService used to new up a client and handler per
        // transfer: because it is scoped, that leaked a connection pool per request and never picked up
        // a DNS change for a host that moved.
        //
        // Timeout and certificate validation come from VsphereOptions, which is what the per-call
        // handler set. The handler is pooled, so toggling SkipGuestFileCertificateValidation now takes
        // effect at the next handler rotation rather than on the next transfer - the same trade the
        // Proxmox clients above already make.
        private static void AddGuestFileClient(this IServiceCollection services)
        {
            services.AddHttpClient(GuestFileClientName, (sp, client) =>
                {
                    var vsphereOptions = sp.GetRequiredService<IOptionsMonitor<VsphereOptions>>().CurrentValue;

                    client.Timeout = vsphereOptions.GuestFileTransferTimeoutMinutes > 0
                        ? TimeSpan.FromMinutes(vsphereOptions.GuestFileTransferTimeoutMinutes)
                        : Timeout.InfiniteTimeSpan;
                })
                .ConfigurePrimaryHttpMessageHandler(sp =>
                {
                    // IOptionsMonitor rather than VsphereOptions: the latter is registered Scoped
                    // (Startup.cs), and this factory runs outside any scope.
                    var vsphereOptions = sp.GetRequiredService<IOptionsMonitor<VsphereOptions>>().CurrentValue;

                    return new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = vsphereOptions.SkipGuestFileCertificateValidation
                            ? (_, _, _, _) => true
                            : null
                    };
                });
        }

        private static void AddIdentityClient(
            this IServiceCollection services,
            IdentityClientOptions identityClientOptions)
        {
            services.AddHttpClient("identity");
        }


        private static void AddPlayerClient(
            this IServiceCollection services,
            ClientOptions clientOptions)
        {
            services.AddHttpClient("player-admin")
                .AddHttpMessageHandler<AuthenticatingHandler>();

            services.AddScoped<IPlayerApiClient, PlayerApiClient>(p =>
            {
                var httpContextAccessor = p.GetRequiredService<IHttpContextAccessor>();
                var httpClientFactory = p.GetRequiredService<IHttpClientFactory>();
                var clientOptions = p.GetRequiredService<ClientOptions>();

                var playerUri = new Uri(clientOptions.urls.playerApi);

                string authHeader = httpContextAccessor.HttpContext.Request.Headers["Authorization"];

                if (authHeader == null)
                {
                    var token = httpContextAccessor.HttpContext.Request.Query["access_token"];
                    authHeader = new AuthenticationHeaderValue("Bearer", token).ToString();
                }

                var httpClient = httpClientFactory.CreateClient();
                httpClient.BaseAddress = playerUri;
                httpClient.DefaultRequestHeaders.Add("Authorization", authHeader);

                var playerApiClient = new PlayerApiClient(httpClient);

                return playerApiClient;
            });
        }

        #endregion

        #region Feature Handlers

        /// <summary>
        /// Finds all non-abstract IFeatureHandler implementations in this assembly and registers each
        /// as a Scoped service by its concrete type. New per-endpoint handlers are picked up
        /// automatically - implementing IFeatureHandler is the only registration step required.
        /// </summary>
        public static void AddFeatureHandlers(this IServiceCollection services)
        {
            var handlerTypes = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => !t.IsAbstract
                    && !t.IsInterface
                    && !t.IsGenericTypeDefinition
                    && typeof(IFeatureHandler).IsAssignableFrom(t));

            foreach (var handlerType in handlerTypes)
            {
                services.AddScoped(handlerType);
            }
        }

        #endregion
    }
}
