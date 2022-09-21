// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options
{
    public class IdentityClientOptions
    {
        public string TokenUrl { get; set; }
        public string ClientId { get; set; }
        public string Scope { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
        public int MaxRetryDelaySeconds { get; set; }
        public int TokenRefreshSeconds { get; set; }
    }
}
