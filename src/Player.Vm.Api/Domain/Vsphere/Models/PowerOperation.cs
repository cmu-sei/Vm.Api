// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public enum PowerOperation
    {
        PowerOn,
        PowerOff,
        Shutdown,
        Reboot,
        Revert
    }
}