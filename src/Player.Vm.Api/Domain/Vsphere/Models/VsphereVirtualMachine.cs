// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using VimClient;

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public class VsphereVirtualMachine
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public string State { get; set; }

        public VirtualMachineToolsStatus VmToolsStatus { get; set; }

        public string HostReference { get; set; }

        public ManagedObjectReference Reference { get; set; }

        public VirtualDevice[] Devices { get; set; }

        public string[] IpAddresses { get; set; }
    }
}
