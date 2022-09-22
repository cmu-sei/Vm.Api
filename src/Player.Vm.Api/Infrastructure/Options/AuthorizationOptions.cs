// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options
{
    public class AuthorizationOptions
    {
        public string Authority { get; set; }
        public string AuthorizationUrl { get; set; }
        public string TokenUrl { get; set; }
        public string AuthorizationScope { get; set; }
        public string PrivilegedScope { get; set; }
        public string ClientId { get; set; }
        public string ClientName { get; set; }
        public string ClientSecret { get; set; }
        public bool RequireHttpsMetadata { get; set; }
        public bool ValidateAudience { get; set; }
        public string[] ValidAudiences { get; set; }
    }
}
