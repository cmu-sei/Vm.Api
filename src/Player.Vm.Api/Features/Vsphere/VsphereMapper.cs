// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Riok.Mapperly.Abstractions;

namespace Player.Vm.Api.Features.Vsphere;

/// <summary>vSphere machines' mappings.</summary>
[Mapper]
public static partial class VsphereMapper
{
    /// <remarks>What vCenter reports. The handler fills the rest, from the stored Vm and the caller.</remarks>
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.Url))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.UserId))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.IsOwner))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.Ticket))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.NetworkCards))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.CanAccessNicConfiguration))]
    public static partial VsphereVirtualMachine ToVsphereVirtualMachine(this Domain.Vsphere.Models.VsphereVirtualMachine source);

    /// <summary>Copies the stored Vm's members over the machine, <c>HasSnapshot</c> included.</summary>
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.State))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.IsOwner))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.Ticket))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.NetworkCards))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.CanAccessNicConfiguration))]
    [MapperIgnoreTarget(nameof(VsphereVirtualMachine.VmToolsStatus))]
    public static partial void ApplyTo(this Vms.Vm source, VsphereVirtualMachine target);
}
