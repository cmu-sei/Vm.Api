// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Proxmox.Models
{
    /// <summary>
    /// One ISO as PVE's storage content index reports it.
    /// </summary>
    /// <param name="VolumeId">
    /// The PVE volume id ("storage:iso/name.iso"), taken verbatim from the listing rather than rebuilt.
    /// This is the token a mount takes, and PVE normalizes uploaded filenames, so a reconstructed id
    /// can differ from the one that actually exists.
    /// </param>
    /// <param name="FileName">The filename portion of <paramref name="VolumeId"/>, still scope-encoded.</param>
    /// <param name="Size">Size in bytes, as reported by PVE.</param>
    public sealed record ProxmoxIsoVolume(string VolumeId, string FileName, long Size);
}
