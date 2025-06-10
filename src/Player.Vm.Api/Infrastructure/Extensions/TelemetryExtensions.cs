// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Infrastructure.Options;
using System;
using System.Collections.Generic;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Player.Vm.Api.Infrastructure.Extensions
{
    public static class TelemetryExtensions
    {
        public static void AddTelemetry(this IServiceCollection services, AuthorizationOptions authOptions)
        {
            var env = "Local";
            var appId = authOptions.ClientId;
            var appName = authOptions.ClientName;
            // configure metrics for grafana
            var otel = services.AddOpenTelemetry();

            // Configure OpenTelemetry Resources with the application name
            otel.ConfigureResource(resource =>
              {
                  resource.AddService(serviceName: $"{appName}");
                  var globalOpenTelemetryAttributes = new List<KeyValuePair<string, object>>
                  {
                      new KeyValuePair<string, object>("env", env),
                      new KeyValuePair<string, object>("appId", appId),
                      new KeyValuePair<string, object>("appName", appName)
                  };
                  resource.AddAttributes(globalOpenTelemetryAttributes);
              });

            // Add Metrics for ASP.NET Core and our custom metrics and export to Prometheus
            otel.WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(appName,
                    "Microsoft.AspNetCore.Hosting",
                    "System.Net.Http")
                .AddPrometheusExporter());
        }
    }
}
