// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;

namespace Player.Vm.Api.Domain.Proxmox.Models
{
    /// <summary>
    /// The parts of a Vm's live Proxmox config the API response is built from, read in one call.
    /// </summary>
    /// <param name="CurrentNetworks">Adapter id to bridge, for adapters that have both.</param>
    /// <param name="HasCdromDrive">
    /// Whether the Vm has a CD/DVD drive an ISO could be mounted into. False for LXC, which has no
    /// optical drive, and false for a QEMU Vm built without one - Proxmox cannot hot-add an IDE drive,
    /// so this is not something a mount could arrange for itself.
    /// </param>
    public sealed record ProxmoxVmConfigSummary(
        Dictionary<string, string> CurrentNetworks,
        bool HasCdromDrive)
    {
        /// <summary>
        /// The snapshot the Vm is running from (the config's <c>parent</c>), or null when it has none.
        /// </summary>
        public string CurrentSnapshot { get; init; }

        /// <summary>
        /// Whether the config turns the QEMU guest agent on. Always false for LXC, which has none. Whether
        /// an agent is actually running in the guest is a separate question, answered by a ping.
        /// </summary>
        public bool AgentEnabled { get; init; }

        /// <summary>
        /// Whether the config's <c>vga</c> property sets <c>clipboard=vnc</c>, without which the guest's
        /// clipboard never reaches the VNC console. Always false for LXC, which has no VGA device.
        /// </summary>
        public bool VncClipboardEnabled { get; init; }
    }
}
