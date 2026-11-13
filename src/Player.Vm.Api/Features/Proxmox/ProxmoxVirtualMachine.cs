// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Player.Vm.Api.Domain.Vsphere.Models;

namespace Player.Vm.Api.Features.Proxmox;

public class ProxmoxVirtualMachine
{
    public Guid Id { get; set; }

    public string Name { get; set; }

    public Guid? UserId { get; set; }

    public NicOptions NetworkCards { get; set; }

    public bool CanAccessNicConfiguration { get; set; }

    // Whether this Vm can accept an ISO at all: QEMU only (an LXC container has no CD-ROM), and only
    // where the deployment has configured Proxmox ISO storage. Lets the UI hide the Mount ISO control
    // rather than offer one that is guaranteed to 400.
    public bool CanMountIso { get; set; }

    // Whether a QEMU guest agent is answering right now (agent/ping), the counterpart of vSphere's
    // VMware Tools status. IP addresses, guest file transfer and guest processes all need it. Always
    // false for LXC, and false when the Vm's config does not enable the agent.
    public bool GuestAgentRunning { get; set; }

    // Whether the Vm has a snapshot to revert to, read live from its config.
    public bool HasSnapshot { get; set; }

    // The snapshot the Vm is running from - what actions/revert reverts to - or null when it has none.
    public string CurrentSnapshot { get; set; }

    // Whether the Vm's vga config sets clipboard=vnc, so the guest's clipboard can reach the console's
    // Copy. Copy also needs spice-vdagent running in the guest, which the config cannot tell us. Always
    // false for LXC.
    public bool VncClipboardEnabled { get; set; }

    // The largest file actions/upload-file accepts, in bytes: the deployment's FileUploadMaxBytes,
    // capped at what PVE's agent/file-write can take. Lets the console refuse a larger file before
    // sending it, since the API can only refuse it once the whole file has arrived.
    public int FileUploadMaxBytes { get; set; }
}
