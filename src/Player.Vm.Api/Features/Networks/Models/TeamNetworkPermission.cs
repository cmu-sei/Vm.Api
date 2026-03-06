// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Networks
{
    public class TeamNetworkPermission
    {
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public VmType ProviderType { get; set; }
        public string ProviderInstanceId { get; set; }
        public string NetworkId { get; set; }
    }
}
