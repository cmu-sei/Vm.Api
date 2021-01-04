// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Player.Vm.Api.Infrastructure.Extensions
{
    public static class CorsPolicyExtensions
    {
        public static CorsOptions UseConfiguredCors(
            this CorsOptions builder,
            IConfiguration section
        )
        {
            CorsPolicyOptions policy = new CorsPolicyOptions();
            ConfigurationBinder.Bind(section, policy);
            builder.AddDefaultPolicy(policy.Build());
            return builder;
        }
    }
    public class CorsPolicyOptions
    {
        public string[] Origins { get; set; }
        public string[] Methods { get; set; }
        public string[] Headers { get; set; }
        public bool AllowAnyOrigin { get; set; }
        public bool AllowAnyMethod { get; set; }
        public bool AllowAnyHeader { get; set; }
        public bool SupportsCredentials { get; set; }

        public CorsPolicy Build()
        {
            CorsPolicyBuilder policy = new CorsPolicyBuilder();
            if (this.AllowAnyOrigin)
                policy.AllowAnyOrigin();
            else
                policy.WithOrigins(this.Origins);

            if (this.AllowAnyHeader)
                policy.AllowAnyHeader();
            else
                policy.WithHeaders(this.Headers);

            if (this.AllowAnyMethod)
                policy.AllowAnyMethod();
            else
                policy.WithMethods(this.Methods);

            if (this.SupportsCredentials)
                policy.AllowCredentials();
            else
                policy.DisallowCredentials();

            return policy.Build();
        }
    }
}
