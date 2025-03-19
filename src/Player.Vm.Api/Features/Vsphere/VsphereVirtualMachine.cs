// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using VimClient;
using Player.Vm.Api.Domain.Vsphere.Models;

namespace Player.Vm.Api.Features.Vsphere
{
    public class VsphereVirtualMachine
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public string Url { get; set; }

        public Guid? UserId { get; set; }

        public bool IsOwner { get; set; }

        public string Ticket { get; set; }

        public string State { get; set; }

        public NicOptions NetworkCards { get; set; }

        public bool CanAccessNicConfiguration { get; set; }

        public List<string> AllowedNetworks { get; set; }

        public VirtualMachineToolsStatus VmToolsStatus { get; set; }
        public bool HasSnapshot { get; set; }
    }
}
