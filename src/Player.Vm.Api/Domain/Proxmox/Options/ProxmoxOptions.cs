// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Proxmox.Options
{
    public class ProxmoxOptions
    {
        public bool Enabled { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Token { get; set; }
        public int StateRefreshIntervalSeconds { get; set; }
    }
}
