// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Player.Vm.Api.Features.Vms
{
    public class VmCreateForm
    {
        [Required]
        public Guid? Id { get; set; }

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
    }
}
