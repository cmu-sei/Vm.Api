// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using NetVimClient;

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public enum VmPowerState
    {
        off,
        running,
        suspended
    }
}