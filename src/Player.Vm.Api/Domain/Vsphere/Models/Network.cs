// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public class Network
    {
        public string Name { get; set; }
        public bool IsDistributed { get; set; }
        public string SwitchId { get; set; }
        public string Reference { get; set; }
    }
}
