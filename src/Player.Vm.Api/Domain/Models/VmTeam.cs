// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;

namespace Player.Vm.Api.Domain.Models
{
    public class VmTeam
    {
        public VmTeam() { }

        public VmTeam(Guid teamId, Guid vmId)
        {
            TeamId = teamId;
            VmId = vmId;
        }

        public Guid TeamId { get; set; }
        public virtual Team Team { get; set; }

        public Guid VmId { get; set; }
        public virtual Vm Vm { get; set; }
    }

    public class VmTeamConfiguration : IEntityTypeConfiguration<VmTeam>
    {
        public void Configure(EntityTypeBuilder<VmTeam> builder)
        {
            builder.HasKey(e => new { e.TeamId, e.VmId });

            builder
                .HasOne(tu => tu.Team)
                .WithMany(t => t.VmTeams)
                .HasForeignKey(tu => tu.TeamId);

            builder
                .HasOne(tu => tu.Vm)
                .WithMany(u => u.VmTeams)
                .HasForeignKey(tu => tu.VmId);
        }
    }
}
