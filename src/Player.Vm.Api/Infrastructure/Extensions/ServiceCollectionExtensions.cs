// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.


using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Player.Api.Client;
using Player.Vm.Api.Infrastructure.HttpHandlers;
using Player.Vm.Api.Infrastructure.OperationFilters;
using Player.Vm.Api.Infrastructure.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Serialization;

namespace Player.Vm.Api.Infrastructure.Extensions
{
    public static class ServiceCollectionExtensions
    {
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
                        Implicit = new OpenApiOAuthFlow
                        {
                            AuthorizationUrl = new Uri(authOptions.AuthorizationUrl),
                            Scopes = new Dictionary<string, string>()
                            {
                                {authOptions.AuthorizationScope, "public api access"}
                            }
                        }
                    }
                });

                c.AddSecurityRequirement(new OpenApiSecurityRequirement()
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "oauth2"
                            },
                            Scheme = "oauth2"
                        },
                        new[] {authOptions.AuthorizationScope}
                    }
                });

                c.EnableAnnotations();
                c.IncludeXmlComments(commentsFile);
                c.CustomSchemaIds(schemaIdStrategy);
                c.OperationFilter<DefaultResponseOperationFilter>();
                c.OperationFilter<JsonIgnoreQueryOperationFilter>();
                c.DocumentFilter<VmUserTeamDocumentFilter>();
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
            ClientOptions clientOptions)
        {
            services.AddHttpClient();
            services.AddIdentityClient(identityClientOptions);
            services.AddPlayerClient(clientOptions);
            services.AddTransient<AuthenticatingHandler>();
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
    }
}
