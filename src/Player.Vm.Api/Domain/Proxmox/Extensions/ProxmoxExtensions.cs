// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Domain.Proxmox.Extensions
{
    public static class ProxmoxExtensions
    {
        /// <summary>
        /// Gets a generic PowerState from a Proxmox virtual machine
        /// </summary>
        public static PowerState GetPowerState(this IClusterResourceVm vm)
        {
            if (vm == null)
                return PowerState.Unknown;

            if (vm.IsRunning)
                return PowerState.On;

            if (vm.IsStopped)
                return PowerState.Off;

            if (vm.IsPaused)
                return PowerState.Suspended;

            return PowerState.Unknown;
        }
    }
}
