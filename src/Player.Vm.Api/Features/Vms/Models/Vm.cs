// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Vms
{
    public class Vm
    {
        /// <summary>
        /// Virtual Machine unique id
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Url to the Vm's console
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// True if this VM's Url has not been explicitly set and a default value has been computed
        /// </summary>
        public bool DefaultUrl { get; set; }

        /// <summary>
        /// The Vm's name
        /// </summary>
        /// <value></value>
        public string Name { get; set; }

        /// <summary>
        /// Id of the Vm's owner if it is a personal Vm
        /// </summary>
        public Guid? UserId { get; set; }

        /// <summary>
        /// A list of networks that a regular user can access
        /// </summary>
        public string[] AllowedNetworks { get; set; }

        /// <summary>
        /// The Vm's last known power state
        /// </summary>
        public PowerState PowerState { get; set; }

        /// <summary>
        /// A list of IP addresses of the Vm
        /// </summary>
        public string[] IpAddresses { get; set; }

        /// <summary>
        /// The Ids of the Team's the Vm is a part of
        /// </summary>
        public IEnumerable<Guid> TeamIds { get; set; }

        /// <summary>
        /// True if this Vm currently has pending tasks (power on, power off, etc)
        /// </summary>
        public bool HasPendingTasks { get; set; }

        /// <summary>
        /// The connection info for connecting to a Vm console through Guacamole.
        /// This is used for non-VMware Vms such as in Azure or AWS.
        /// </summary>
        public ConsoleConnectionInfo ConsoleConnectionInfo { get; set; }

        /// <summary>
        /// The Type of hypervisor or platform this VM runs on.
        /// </summary>
        public VmType Type { get; set; }

        /// <summary>
        /// Information for connecting to a Proxmox Vm.
        /// </summary>
        public ProxmoxVmInfo ProxmoxVmInfo { get; set; }
    }
}
