// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using VimClient;

namespace Player.Vm.Api.Domain.Vsphere.Models;

public class VsphereAggregate
{
    public VsphereAggregate(VsphereConnection connection, ManagedObjectReference machineReference)
    {
        this.Connection = connection;
        this.MachineReference = machineReference;
    }

    public VsphereConnection Connection { get; set; }
    public ManagedObjectReference MachineReference { get; set; }
}
