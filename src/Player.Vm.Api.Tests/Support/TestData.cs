// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api's migrations seed no rows (no roles, no permissions: those live in player.api), so this holds
// only object mothers. Team and View ids are not foreign keys here (teams live in player.api; VmTeam's
// only relationship is to Vm), so an arbitrary Guid is valid seed data for them.

using System;
using Player.Vm.Api.Domain.Models;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests.Support;

/// <summary>Object mothers for the entities most tests need.</summary>
public static class TestData
{
    /// <summary>
    /// The View every <see cref="IsolationProbe"/> network belongs to. Fixed, so that two probes with one
    /// network id collide on <c>view_networks</c>' unique index.
    /// </summary>
    public static readonly Guid ProbeViewId = new("6c0e5d2a-0000-4000-8000-000000000001");

    /// <summary>
    /// A vSphere Vm that passes every gate in <c>BulkPowerOperation</c>: a supported type, a power state
    /// the vSphere path accepts (Unknown is rejected outright), and a team to authorize against.
    /// </summary>
    public static VmEntity VsphereVm(
        Guid? teamId = null,
        PowerState powerState = PowerState.Off,
        bool hasSnapshot = false)
    {
        var id = Guid.NewGuid();

        return new VmEntity
        {
            Id = id,
            Name = $"vm-{id}",
            Type = VmType.Vsphere,
            PowerState = powerState,
            HasSnapshot = hasSnapshot,
            VmTeams = [new VmTeam(teamId ?? Guid.NewGuid(), id)]
        };
    }

    /// <summary>
    /// A Proxmox Vm that passes every gate in <c>BulkPowerOperation</c>, as <see cref="VsphereVm"/> is for
    /// the other hypervisor. It needs no particular power state, because the Proxmox path does not gate
    /// on one; it does need the <c>ProxmoxVmInfo</c> row, which the real
    /// <c>ProxmoxService.BulkPowerOperation</c> looks the Vm up by.
    /// </summary>
    public static VmEntity ProxmoxVm(
        Guid? teamId = null,
        PowerState powerState = PowerState.Off,
        int vmid = 100)
    {
        var id = Guid.NewGuid();

        return new VmEntity
        {
            Id = id,
            Name = $"vm-{id}",
            Type = VmType.Proxmox,
            PowerState = powerState,
            VmTeams = [new VmTeam(teamId ?? Guid.NewGuid(), id)],
            ProxmoxVmInfo = new ProxmoxVmInfo { Id = vmid, Node = "pve-1" },
        };
    }

    /// <summary>
    /// A vSphere network of <see cref="ProbeViewId"/> identified by <paramref name="networkId"/>: the
    /// harness's isolation probe, because <c>(view_id, provider_type, provider_instance_id,
    /// network_id)</c> is uniquely indexed.
    /// </summary>
    public static ViewNetwork IsolationProbe(string networkId) =>
        new()
        {
            Id = Guid.NewGuid(),
            ViewId = ProbeViewId,
            ProviderType = VmType.Vsphere,
            ProviderInstanceId = "",
            NetworkId = networkId,
            Name = networkId,
        };
}
