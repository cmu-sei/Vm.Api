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
        public string NetworkName { get; set; }
        public string Type { get; set; } = "";
        public string ExternalId { get; set; } = "";
    }

    public class TeamNetworkPermissionConfiguration : IEntityTypeConfiguration<TeamNetworkPermission>
    {
        public void Configure(EntityTypeBuilder<TeamNetworkPermission> builder)
        {
            builder.HasIndex(x => new { x.TeamId, x.NetworkName, x.Type, x.ExternalId }).IsUnique();
        }
    }
}
