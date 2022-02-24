// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Logging;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Services.HealthChecks;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Shared.Behaviors;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Infrastructure.DbInterceptors;
using Player.Vm.Api.Infrastructure.Exceptions.Middleware;
using Player.Vm.Api.Infrastructure.Extensions;
using Player.Vm.Api.Infrastructure.Options;
using AuthorizationOptions = Player.Vm.Api.Infrastructure.Options.AuthorizationOptions;
using Player.Vm.Api.Infrastructure.Constants;

namespace Player.Vm.Api
{
    public class Startup
    {
        private readonly AuthorizationOptions _authOptions = new AuthorizationOptions();
        private readonly ClientOptions _clientOptions = new ClientOptions();
        private readonly IdentityClientOptions _identityClientOptions = new IdentityClientOptions();
        private const string _routePrefix = "api";
        private string _pathbase;

        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
            Configuration.Bind("ClientSettings", _clientOptions);
            Configuration.Bind("IdentityClient", _identityClientOptions);
            Configuration.Bind("Authorization", _authOptions);
            _pathbase = Configuration["PathBase"];
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<TaskServiceHealthCheck>();
            services.AddSingleton<ConnectionServiceHealthCheck>();
            services.AddHealthChecks()
                .AddCheck<TaskServiceHealthCheck>(
                    "task_service_responsive",
                    failureStatus: HealthStatus.Unhealthy,
                    tags: new[] { "live" })
                .AddCheck<ConnectionServiceHealthCheck>(
                    "connection_service_responsive",
                    failureStatus: HealthStatus.Unhealthy,
                    tags: new[] { "live" });

            var provider = Configuration["Database:Provider"];
            var vmLoggingEnabled = bool.Parse((Configuration["VmUsageLogging:Enabled"]));
            switch (provider)
            {
                case "InMemory":
                    services.AddDbContextPool<VmContext>((serviceProvider, optionsBuilder) => optionsBuilder
                        .AddInterceptors(serviceProvider.GetRequiredService<EventTransactionInterceptor>())
                        .UseInMemoryDatabase("vm"));
                    break;
                case "Sqlite":
                case "SqlServer":
                case "PostgreSQL":
                    services.AddDbContextPool<VmContext>((serviceProvider, optionsBuilder) => optionsBuilder
                        .AddInterceptors(serviceProvider.GetRequiredService<EventTransactionInterceptor>())
                        .UseConfiguredDatabase(Configuration));
                    
                    if (vmLoggingEnabled) 
                    {
                        var vmLoggingConnectionString = Configuration["VmUsageLogging:PostgreSql"].Trim();

                        /* Note:  When using multiple DB contexts, dotnet ef migrations must specify which context:  ie:
                        dotnet ef migrations add "VmLoggingDb Initial" --context VmLoggingContext -o Data/Migrations/Postgres/VmLogging
                        */
                        services.AddDbContextPool<VmLoggingContext>(
                            options => options.UseNpgsql(vmLoggingConnectionString));
                    }
                    break;
            }

            var connectionString = Configuration.GetConnectionString(Configuration.GetValue<string>("Database:Provider", "Sqlite").Trim());
            switch (provider)
            {
                case "Sqlite":
                    services.AddHealthChecks().AddSqlite(connectionString, tags: new[] { "ready", "live" });
                    break;
                case "SqlServer":
                    services.AddHealthChecks().AddSqlServer(connectionString, tags: new[] { "ready", "live" });
                    break;
                case "PostgreSQL":
                    services.AddHealthChecks().AddNpgSql(connectionString, tags: new[] { "ready", "live" });
                    break;
            }

            services.AddOptions()
                .Configure<DatabaseOptions>(Configuration.GetSection("Database"))
                .AddScoped(config => config.GetService<IOptionsMonitor<DatabaseOptions>>().CurrentValue);

            IConfiguration isoConfig = Configuration.GetSection("IsoUpload");
            IsoUploadOptions isoOptions = new IsoUploadOptions();
            isoConfig.Bind(isoOptions);

            services.AddOptions()
                .Configure<IsoUploadOptions>(isoConfig)
                .AddScoped(config => config.GetService<IOptionsMonitor<IsoUploadOptions>>().CurrentValue);

            services
                .Configure<ClientOptions>(Configuration.GetSection("ClientSettings"))
                .AddScoped(config => config.GetService<IOptionsMonitor<ClientOptions>>().CurrentValue);

            services
                .Configure<VsphereOptions>(Configuration.GetSection("Vsphere"))
                .AddScoped(config => config.GetService<IOptionsSnapshot<VsphereOptions>>().Value);

            services
                .Configure<RewriteHostOptions>(Configuration.GetSection("RewriteHost"))
                .AddScoped(config => config.GetService<IOptionsSnapshot<RewriteHostOptions>>().Value);

            services
                .Configure<IdentityClientOptions>(Configuration.GetSection("IdentityClient"))
                .AddScoped(config => config.GetService<IOptionsSnapshot<IdentityClientOptions>>().Value);

            services
                .Configure<ConsoleUrlOptions>(Configuration.GetSection("ConsoleUrls"))
                .AddScoped(config => config.GetService<IOptionsSnapshot<ConsoleUrlOptions>>().Value);
            
            services
                .Configure<VmUsageLoggingOptions>(Configuration.GetSection("VmUsageLogging"))
                .AddScoped(config => config.GetService<IOptionsSnapshot<VmUsageLoggingOptions>>().Value);

            services.AddCors(options => options.UseConfiguredCors(Configuration.GetSection("CorsPolicy")));
            services.AddMvc()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
                    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                });

            services.AddAuthorization(options =>
            {
                var policyBuilder = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();

                foreach (var scope in _authOptions.AuthorizationScope.Split(' '))
                {
                    policyBuilder.RequireScope(scope);
                }

                options.DefaultPolicy = policyBuilder.Build();

                options.AddPolicy(Constants.PrivilegedAuthorizationPolicy, builder => builder
                    .RequireAuthenticatedUser()
                    .RequireScope(_authOptions.PrivilegedScope)
                );
            });

            services.AddSignalR()
               .AddJsonProtocol(options =>
               {
                   options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                   options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
               });

            // allow upload of large files
            services.Configure<FormOptions>(x =>
            {
                x.ValueLengthLimit = int.MaxValue;
                x.MultipartBodyLengthLimit = isoOptions.MaxFileSize;
            });

            services.AddSwagger(_authOptions);

            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = _authOptions.Authority;
                options.RequireHttpsMetadata = _authOptions.RequireHttpsMetadata;
                options.SaveToken = true;
                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateAudience = false,
                    ValidateIssuer = true
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // If the request is for our hub...
                        var path = context.HttpContext.Request.Path;
                        var accessToken = context.Request.Query["access_token"];

                        if (!string.IsNullOrEmpty(accessToken) &&
                            (path.StartsWithSegments("/hubs")))
                        {
                            // Read the token out of the query string
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

            services.AddRouting(options =>
            {
                options.LowercaseUrls = true;
            });

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddScoped<IPrincipal>(p => p.GetService<IHttpContextAccessor>().HttpContext.User);

            services.AddScoped<IVmService, VmService>();
            services.AddScoped<IPlayerService, PlayerService>();
            services.AddScoped<IViewService, ViewService>();
            services.AddScoped<IPermissionsService, PermissionsService>();
            services.AddSingleton<CallbackBackgroundService>();
            services.AddSingleton<IHostedService>(x => x.GetService<CallbackBackgroundService>());
            services.AddSingleton<ICallbackBackgroundService>(x => x.GetService<CallbackBackgroundService>());
            services.AddSingleton<IAuthenticationService, AuthenticationService>();
            services.AddSingleton<IActiveVirtualMachineService, ActiveVirtualMachineService>();
            services.AddSingleton<IVmUsageLoggingService, VmUsageLoggingService>();

            // Vsphere Services
            services.AddSingleton<ConnectionService>();
            services.AddSingleton<IHostedService>(x => x.GetService<ConnectionService>());
            services.AddSingleton<IConnectionService>(x => x.GetService<ConnectionService>());
            services.AddScoped<IVsphereService, VsphereService>();
            services.AddSingleton<TaskService>();
            services.AddSingleton<IHostedService>(x => x.GetService<TaskService>());
            services.AddSingleton<ITaskService>(x => x.GetService<TaskService>());
            services.AddSingleton<MachineStateService>();
            services.AddSingleton<IHostedService>(x => x.GetService<MachineStateService>());
            services.AddSingleton<IMachineStateService>(x => x.GetService<MachineStateService>());

            services.AddTransient<EventTransactionInterceptor>();

            services.AddAutoMapper(typeof(Startup));
            services.AddMediatR(typeof(Startup).GetTypeInfo().Assembly);
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CheckTasksBehavior<,>));

            services.AddMemoryCache();

            services.AddApiClients(identityClientOptions: _identityClientOptions, clientOptions: _clientOptions);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                IdentityModelEventSource.ShowPII = true;
            }

            app.UsePathBase(_pathbase);
            app.UseCustomExceptionHandler();
            app.UseRouting();
            app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHub<ProgressHub>("/hubs/progress").RequireAuthorization();
                endpoints.MapHub<VmHub>("/hubs/vm").RequireAuthorization();

                endpoints.MapHealthChecks($"/{_routePrefix}/health/ready", new HealthCheckOptions()
                {
                    Predicate = (check) => check.Tags.Contains("ready"),
                });

                endpoints.MapHealthChecks($"/{_routePrefix}/health/live", new HealthCheckOptions()
                {
                    Predicate = (check) => check.Tags.Contains("live"),
                });
            });

            app.UseSwagger();

            // Enable middleware to serve swagger-ui (HTML, JS, CSS, etc.), specifying the Swagger JSON endpoint.
            app.UseSwaggerUI(c =>
            {
                c.RoutePrefix = _routePrefix;
                c.SwaggerEndpoint($"{_pathbase}/swagger/v1/swagger.json", "Player VM API V1");
                c.OAuthClientId(_authOptions.ClientId);
                c.OAuthClientSecret(_authOptions.ClientSecret);
                c.OAuthAppName(_authOptions.ClientName);
                c.OAuthUsePkce();
            });
        }
    }
}
