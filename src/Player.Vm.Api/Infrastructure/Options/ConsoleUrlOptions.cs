// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options
{
    public class ConsoleUrlOptions
    {
        public VsphereConsoleUrlOptions Vsphere { get; set; }
        public GuacamoleConsoleUrlOptions Guacamole { get; set; }
        public ProxmoxConsoleUrlOptions Proxmox { get; set; }
        public string DefaultUrl { get; set; }
    }

    public class VsphereConsoleUrlOptions
    {
        public string Url { get; set; }
    }

    public class GuacamoleConsoleUrlOptions
    {
        public string ProviderName { get; set; }
    }

    public class ProxmoxConsoleUrlOptions
    {
        public string Url { get; set; }
    }
}
