// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Player.Vm.Api.Features.Vms
{
    public class VmCreateForm
    {
        public Guid? Id { get; set; } = Guid.Empty;

        public string Url { get; set; }

        [Required]
        public string Name { get; set; }

        [Required]
        public IEnumerable<Guid> TeamIds { get; set; }

        public Guid? UserId { get; set; }

        public string[] AllowedNetworks { get; set; }

        /// <summary>
        /// The connection info for connecting to a Vm console through Guacamole.
        /// This is used for non-VMware Vms such as in Azure or AWS.
        /// </summary>
        public ConsoleConnectionInfo ConsoleConnectionInfo { get; set; }

        /// <summary>
        /// For Proxmox Vms only. Necessary information to connect to this Vm.
        /// </summary>
        public ProxmoxVmInfo ProxmoxVmInfo { get; set; }
    }
}
