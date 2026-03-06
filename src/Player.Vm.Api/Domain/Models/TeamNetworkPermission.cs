// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Player.Vm.Api.Domain.Models
{
    public class TeamNetworkPermission
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public VmType ProviderType { get; set; } = VmType.Unknown;
        public string ProviderInstanceId { get; set; } = "";
        public string NetworkId { get; set; } = "";
    }

    public class TeamNetworkPermissionConfiguration : IEntityTypeConfiguration<TeamNetworkPermission>
    {
        public void Configure(EntityTypeBuilder<TeamNetworkPermission> builder)
        {
            builder.HasIndex(x => new { x.TeamId, x.ProviderType, x.ProviderInstanceId, x.NetworkId }).IsUnique();
        }
    }
}
