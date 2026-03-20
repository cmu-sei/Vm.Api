// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Player.Vm.Api.Domain.Models
{
    public class ViewNetwork
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid ViewId { get; set; }
        public VmType ProviderType { get; set; } = VmType.Unknown;
        public string ProviderInstanceId { get; set; } = "";
        public string NetworkId { get; set; } = "";
        public string Name { get; set; } = "";
        public Guid[] TeamIds { get; set; } = [];
    }

    public class ViewNetworkConfiguration : IEntityTypeConfiguration<ViewNetwork>
    {
        public void Configure(EntityTypeBuilder<ViewNetwork> builder)
        {
            builder.HasIndex(x => new { x.ViewId, x.ProviderType, x.ProviderInstanceId, x.NetworkId }).IsUnique();
        }
    }
}
