// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Riok.Mapperly.Abstractions;

namespace Player.Vm.Api.Features.Proxmox;

/// <summary>Proxmox consoles' mappings.</summary>
[Mapper]
public static partial class ProxmoxMapper
{
    public static partial GetConsole.ProxmoxConsole ToProxmoxConsole(this Domain.Proxmox.Models.ProxmoxConsole source);
}
