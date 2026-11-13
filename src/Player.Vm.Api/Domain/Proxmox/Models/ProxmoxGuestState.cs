// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Proxmox.Models
{
    /// <summary>
    /// The per-Vm state the Proxmox state poller reads beyond what /cluster/resources reports.
    /// </summary>
    public sealed record ProxmoxGuestState
    {
        /// <summary>
        /// The snapshot the Vm is currently running from - the <c>parent</c> field of its config - or
        /// null when it has none.
        /// </summary>
        public string CurrentSnapshot { get; init; }

        /// <summary>
        /// Whether the Vm has a snapshot to revert to. PVE's snapshot list always carries a synthetic
        /// "current" entry, so "has a snapshot" is read from the config's <c>parent</c> instead: it is set
        /// whenever a real snapshot lies under the running state.
        /// </summary>
        public bool HasSnapshot => !string.IsNullOrEmpty(CurrentSnapshot);

        /// <summary>
        /// The guest's addresses, loopback and link-local left out, in the bare form vSphere's are stored
        /// in. Empty for a Vm that is off or whose guest agent is not configured or not running; null when
        /// the guest did not answer in time, so the stored value stands.
        /// </summary>
        public string[] IpAddresses { get; init; }
    }
}
