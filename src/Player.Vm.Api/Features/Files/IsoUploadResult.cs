// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Runtime.Serialization;

namespace Player.Vm.Api.Features.Files
{
    [DataContract(Name = "IsoUploadResult")]
    public class IsoUploadResult
    {
        // Summary of the upload outcome, naming the hypervisor TYPE of anything that failed. Never
        // contains host or cluster identifiers - the Files tab already shows users which hypervisors
        // are missing a file, so the type adds nothing they cannot see, but the instance address and
        // the underlying reason stay in the server logs.
        public string Message { get; set; }

        // Number of hosts the ISO failed to upload to (0 when fully successful). Counts only -
        // which hosts failed is recorded in server logs for admins, not exposed to app users.
        public int FailedHostCount { get; set; }

        // Total number of hosts targeted by the upload.
        public int TotalHostCount { get; set; }

        // True when the operation did not fully succeed everywhere: a hypervisor it failed on outright,
        // or some of a hypervisor's hosts. One flag rather than several counts, because that is the only
        // question a client has: the Files tab shows which hypervisors are missing a file, and Message
        // names them. The host counts above are kept for admin-facing detail.
        //
        // Cannot be derived from FailedHostCount: the storage-backed write modes (vSphere over NFS,
        // Proxmox) report no per-host tally at all, so a total failure there leaves both counts at 0.
        public bool PartialFailure { get; set; }
    }
}
