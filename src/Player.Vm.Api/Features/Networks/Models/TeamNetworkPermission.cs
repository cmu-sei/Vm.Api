// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Player.Vm.Api.Features.Networks
{
    public class TeamNetworkPermission
    {
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public string NetworkName { get; set; }
        public string Type { get; set; }
        public string ExternalId { get; set; }
    }
}
