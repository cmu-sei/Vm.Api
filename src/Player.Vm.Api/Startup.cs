// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Security.Principal;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
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
using Crucible.Common.EntityEvents.Extensions;
using Player.Vm.Api.Infrastructure.Exceptions.Middleware;
using Player.Vm.Api.Infrastructure.Extensions;
using Player.Vm.Api.Infrastructure.Options;
using AuthorizationOptions = Player.Vm.Api.Infrastructure.Options.AuthorizationOptions;
using Player.Vm.Api.Infrastructure.Constants;
using System.Linq;
using System.Collections.Generic;
using Player.Vm.Api.Infrastructure.ClaimsTransformers;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Proxmox.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Player.Vm.Api.Infrastructure.Authorization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Player.Vm.Api.Infrastructure.HttpHandlers;

namespace Player.Vm.Api;

public class Startup
{
    private readonly AuthorizationOptions _authOptions = new();
    private readonly ClientOptions _clientOptions = new();
    private readonly SignalROptions _signalROptions = new();
    private readonly IdentityClientOptions _identityClientOptions = new();
    private const string _routePrefix = "api";
    private string _pathbase;
    private readonly TelemetryOptions _telemetryOptions = new();
    private readonly HealthChecksUIOptions _healthChecksUIOptions = new();

    public IConfiguration Configuration { get; }

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
        Configuration.Bind("ClientSettings", _clientOptions);
        Configuration.Bind("IdentityClient", _identityClientOptions);
        Configuration.Bind("Authorization", _authOptions);
        Configuration.GetSection("SignalR").Bind(_signalROptions);
        Configuration.GetSection("Telemetry").Bind(_telemetryOptions);
        Configuration.GetSection("HealthChecksUI").Bind(_healthChecksUIOptions);
        _pathbase = Configuration["PathBase"];
    }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<TaskServiceHealthCheck>();
        services.AddSingleton<ConnectionServiceHealthCheck>();
        services.AddHealthChecks()
            .AddCheck<TaskServiceHealthCheck>(
                "task_service",
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "live" })
            .AddCheck<ConnectionServiceHealthCheck>(
                "connection_service",
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "ready" });

        if (_healthChecksUIOptions.Enabled)
        {
            services
                .AddHealthChecksUI(options =>
                {
                    options.UseApiEndpointDelegatingHandler<LocalhostRedirectHandler>();
                })
                .AddInMemoryStorage();
        }

        var provider = Configuration["Database:Provider"];
        var vmLoggingEnabled = bool.Parse((Configuration["VmUsageLogging:Enabled"]));
        switch (provider)
        {
            case "InMemory":
                services.AddEventPublishingDbContextFactory<VmContext>((serviceProvider, optionsBuilder) => optionsBuilder
                    .UseInMemoryDatabase("vm"));
                break;
            case "Sqlite":
            case "SqlServer":
            case "PostgreSQL":
                services.AddEventPublishingDbContextFactory<VmContext>((serviceProvider, optionsBuilder) => optionsBuilder
                    .UseConfiguredDatabase(Configuration));

                var vmLoggingConnectionString = Configuration["VmUsageLogging:PostgreSql"].Trim();

                /* Note:  When using multiple DB contexts, dotnet ef migrations must specify which context:  ie:
                dotnet ef migrations add "VmLoggingDb Initial" --context VmLoggingContext -o Data/Migrations/Postgres/VmLogging
                */
                services.AddDbContextPool<VmLoggingContext>(
                    options => options.UseNpgsql(vmLoggingConnectionString));

                if (vmLoggingEnabled)
                {
                    services.AddScoped<IVmUsageLoggingService, VmUsageLoggingService>();
                }
                else
                {
                    services.AddSingleton<IVmUsageLoggingService, DisabledVmUsageLoggingService>();
                }

                break;
        }

        var connectionString = Configuration.GetConnectionString(Configuration.GetValue<string>("Database:Provider", "Sqlite").Trim());
        const string dbHealthCheckName = "database";
        string[] dbHealthCheckTags = ["ready"];
        switch (provider)
        {
            case "Sqlite":
                services.AddHealthChecks().AddSqlite(connectionString, name: dbHealthCheckName, tags: dbHealthCheckTags);
                break;
            case "SqlServer":
                services.AddHealthChecks().AddSqlServer(connectionString, name: dbHealthCheckName, tags: dbHealthCheckTags);
                break;
            case "PostgreSQL":
                services.AddHealthChecks().AddNpgSql(connectionString, name: dbHealthCheckName, tags: dbHealthCheckTags);
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

        services
            .Configure<ProxmoxOptions>(Configuration.GetSection("Proxmox"))
            .AddScoped(config => config.GetService<IOptionsSnapshot<ProxmoxOptions>>().Value);

        services.AddOptions()
            .Configure<HealthChecksUIOptions>(Configuration.GetSection("HealthChecksUI"))
            .AddScoped(config => config.GetService<IOptionsMonitor<HealthChecksUIOptions>>().CurrentValue);

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
                policyBuilder.RequireClaim("scope", scope);
            }

            options.DefaultPolicy = policyBuilder.Build();

            options.AddPolicy(Constants.PrivilegedAuthorizationPolicy, builder => builder
                .RequireAuthenticatedUser()
                .RequireScope(_authOptions.PrivilegedScope)
            );
        });

        services.AddSignalR(o => o.StatefulReconnectBufferSize = _signalROptions.StatefulReconnectBufferSizeBytes)
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

        JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = _authOptions.Authority;
            options.RequireHttpsMetadata = _authOptions.RequireHttpsMetadata;
            options.SaveToken = true;

            string[] validAudiences;

            if (_authOptions.ValidAudiences != null && _authOptions.ValidAudiences.Any())
            {
                validAudiences = _authOptions.ValidAudiences;
            }
            else
            {
                var list = new List<string>() { _authOptions.PrivilegedScope };
                list.AddRange(_authOptions.AuthorizationScope.Split(' '));
                validAudiences = list.ToArray();
            }

            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateAudience = _authOptions.ValidateAudience,
                ValidAudiences = validAudiences
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
        services.AddScoped<IPrincipal>(p => p.GetService<IHttpContextAccessor>()?.HttpContext?.User);

        services.AddScoped<IVmService, VmService>();
        services.AddScoped<IPlayerService, PlayerService>();
        services.AddScoped<IViewService, ViewService>();
        services.AddSingleton<CallbackBackgroundService>();
        services.AddSingleton<IHostedService>(x => x.GetService<CallbackBackgroundService>());
        services.AddSingleton<ICallbackBackgroundService>(x => x.GetService<CallbackBackgroundService>());
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<IActiveVirtualMachineService, ActiveVirtualMachineService>();
        services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, ClaimsTransformer>();
        services.AddScoped<IIdentityResolver, IdentityResolver>();
        services.AddTransient<LocalhostRedirectHandler>();

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

        // Proxmox Services
        services.AddScoped<IProxmoxService, ProxmoxService>();
        services.AddSingleton<ProxmoxStateService>();
        services.AddSingleton<IHostedService>(x => x.GetService<ProxmoxStateService>());
        services.AddSingleton<IProxmoxStateService>(x => x.GetService<ProxmoxStateService>());

        services.AddAutoMapper(typeof(Startup));
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(typeof(Startup).Assembly));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CheckTasksBehavior<,>));

        services.AddMemoryCache();

        services.AddApiClients(identityClientOptions: _identityClientOptions, clientOptions: _clientOptions);
        services.AddSingleton<TelemetryService>();
        var metricsBuilder = services.AddOpenTelemetry()
            .WithMetrics(builder =>
            {
                builder
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("TelemetryService"))
                    .AddMeter
                    (
                        TelemetryService.VmConsolesMeterName
                    )
                    .AddPrometheusExporter();
                if (_telemetryOptions.AddRuntimeInstrumentation)
                {
                    builder.AddRuntimeInstrumentation();
                }
                if (_telemetryOptions.AddProcessInstrumentation)
                {
                    builder.AddProcessInstrumentation();
                }
                if (_telemetryOptions.AddAspNetCoreInstrumentation)
                {
                    builder.AddAspNetCoreInstrumentation();
                }
                if (_telemetryOptions.AddHttpClientInstrumentation)
                {
                    builder.AddHttpClientInstrumentation();
                }
                if (_telemetryOptions.UseMeterMicrosoftAspNetCoreHosting)
                {
                    builder.AddMeter("Microsoft.AspNetCore.Hosting");
                }
                if (_telemetryOptions.UseMeterMicrosoftAspNetCoreServerKestrel)
                {
                    builder.AddMeter("Microsoft.AspNetCore.Server.Kestrel");
                }
                if (_telemetryOptions.UseMeterSystemNetHttp)
                {
                    builder.AddMeter("System.Net.Http");
                }
                if (_telemetryOptions.UseMeterSystemNetNameResolution)
                {
                    builder.AddMeter("System.Net.NameResolution");
                }
            }
        );
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
            endpoints.MapHub<ProgressHub>("/hubs/progress", options =>
                    options.AllowStatefulReconnects = _signalROptions.EnableStatefulReconnect
            ).RequireAuthorization();

            endpoints.MapHub<VmHub>("/hubs/vm", options =>
                options.AllowStatefulReconnects = _signalROptions.EnableStatefulReconnect
            ).RequireAuthorization();

            endpoints.MapPrometheusScrapingEndpoint().RequireAuthorization();

            if (_healthChecksUIOptions.Enabled)
            {
                endpoints.MapHealthChecksUI(options =>
                {
                    options.UIPath = _healthChecksUIOptions.Path;
                    options.UseRelativeApiPath = false;
                    options.UseRelativeResourcesPath = false;
                    options.UseRelativeWebhookPath = false;
                    options.AddCustomStylesheet("wwwroot/css/healthchecks.css");
                    options.AsideMenuOpened = false;
                });
            }
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
