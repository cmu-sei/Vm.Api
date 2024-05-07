// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;

namespace Player.Vm.Api.Domain.Models;

public class VmUser
{
    [Key]
    public Guid UserId { get; set; }
    public Guid LastVmId { get; set; }
    public virtual Vm LastVm { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public VmUser(Guid userId, Guid lastVmId)
    {
        UserId = userId;
        LastVmId = lastVmId;
        LastSeen = DateTimeOffset.UtcNow;
    }
}
