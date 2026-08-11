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

    public NicOptions NetworkCards { get; set; }

    public bool CanAccessNicConfiguration { get; set; }

    // Whether this Vm can accept an ISO at all: QEMU only (an LXC container has no CD-ROM), and only
    // where the deployment has configured Proxmox ISO storage. Lets the UI hide the Mount ISO control
    // rather than offer one that is guaranteed to 400.
    public bool CanMountIso { get; set; }
}
