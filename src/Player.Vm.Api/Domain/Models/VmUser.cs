// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Player.Vm.Api.Domain.Models;

public class VmUser
{
    public Guid UserId { get; set; }
    public Guid TeamId { get; set; }
    public Guid LastVmId { get; set; }
    public virtual Vm LastVm { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public VmUser(Guid userId, Guid lastVmId, Guid teamId, DateTimeOffset lastSeen)
    {
        UserId = userId;
        TeamId = teamId;
        LastVmId = lastVmId;
        LastSeen = lastSeen;
    }

    public class VmUserConfiguration : IEntityTypeConfiguration<VmUser>
    {
        public void Configure(EntityTypeBuilder<VmUser> builder)
        {
            builder.HasKey(x => new { x.UserId, x.TeamId });
        }
    }
}
