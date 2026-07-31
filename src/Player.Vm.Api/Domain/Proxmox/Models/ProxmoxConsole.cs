// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Domain.Proxmox.Models
{
    public class ProxmoxConsole
    {
        public string Url { get; set; }
        public string Ticket { get; set; }

        /// <summary>
        /// The live power state read from Proxmox while building the console. A Vm that is not
        /// running has no console to connect to, so Url and Ticket are null and this is the only
        /// populated field - that is a normal response rather than an error, so the client can
        /// show a powered off state instead of retrying a connection that cannot succeed.
        /// </summary>
        public PowerState PowerState { get; set; }
    }
}