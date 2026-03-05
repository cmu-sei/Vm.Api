// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Networks
{
    public class ViewNetworkDto
    {
        public Guid Id { get; set; }
        public Guid ViewId { get; set; }
        public VmType ProviderType { get; set; }
        public string ProviderInstanceId { get; set; }
        public string NetworkId { get; set; }
        public string Name { get; set; }
        public Guid[] TeamIds { get; set; } = [];
    }
}
