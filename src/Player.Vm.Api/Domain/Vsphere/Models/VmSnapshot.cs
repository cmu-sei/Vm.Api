// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public class VmSnapshot
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreateTime { get; set; }
        public string State { get; set; }
        public bool IsCurrent { get; set; }
        public int Depth { get; set; }
    }
}
