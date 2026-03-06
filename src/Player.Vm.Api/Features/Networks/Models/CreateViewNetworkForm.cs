// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Networks
{
    public class CreateViewNetworkForm
    {
        [Required]
        public VmType ProviderType { get; set; }
        [Required]
        public string ProviderInstanceId { get; set; }
        [Required]
        public string NetworkId { get; set; }
        [Required]
        public string Name { get; set; }
        public Guid[] TeamIds { get; set; } = [];
    }
}
