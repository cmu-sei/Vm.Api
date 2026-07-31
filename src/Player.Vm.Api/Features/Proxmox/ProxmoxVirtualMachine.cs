// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Player.Vm.Api.Domain.Vsphere.Models;

namespace Player.Vm.Api.Features.Proxmox;

public class ProxmoxVirtualMachine
{
    public Guid Id { get; set; }

    public string Name { get; set; }

    public Guid? UserId { get; set; }

    public bool IsOwner { get; set; }

    public NicOptions NetworkCards { get; set; }

    public bool CanAccessNicConfiguration { get; set; }
}
