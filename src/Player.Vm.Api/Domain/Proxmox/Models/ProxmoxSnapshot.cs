// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Proxmox.Models
{
    public class ProxmoxSnapshot
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Parent { get; set; }
        public bool VmState { get; set; }
        public long? SnapTime { get; set; }
    }
}
