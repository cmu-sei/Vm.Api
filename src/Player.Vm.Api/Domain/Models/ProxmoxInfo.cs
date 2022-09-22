// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Player.Vm.Api.Domain.Models
{
    public class ProxmoxVmInfo
    {
        public Guid VmId { get; set; }
        public int Id { get; set; }
        public string Node { get; set; }
        public ProxmoxVmType Type { get; set; } = ProxmoxVmType.QEMU;
    }

    public enum ProxmoxVmType
    {
        QEMU,
        LXC
    }
}
