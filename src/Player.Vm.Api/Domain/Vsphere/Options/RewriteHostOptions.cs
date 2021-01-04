// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Vsphere.Options
{
    public class RewriteHostOptions
    {
        public bool RewriteHost { get; set; }
        public string RewriteHostUrl { get; set; }
        public string RewriteHostQueryParam { get; set; }
    }
}