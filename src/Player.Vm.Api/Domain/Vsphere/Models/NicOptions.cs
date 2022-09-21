// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public class NicOptions
    {
        public List<string> AvailableNetworks { get; set; }
        public Dictionary<string, string> CurrentNetworks { get; set; }
    }
}