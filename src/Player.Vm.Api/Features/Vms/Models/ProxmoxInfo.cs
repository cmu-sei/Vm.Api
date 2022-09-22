// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Vms
{
    public class ProxmoxVmInfo
    {
        /// <summary>
        /// The unique integer Id of the VM in Proxmox
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The node that this VM is currently running on
        /// </summary>
        public string Node { get; set; }

        /// <summary>
        /// The Type of VM in Proxmox
        /// </summary>
        public ProxmoxVmType Type { get; set; }
    }
}
