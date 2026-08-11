// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Runtime.Serialization;
using Player.Vm.Api.Domain.Models;

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

        // Number of hypervisor providers the operation failed on outright (0 when fully successful).
        // Distinct from the host counts, which tally targets *within* a provider: with vSphere and
        // Proxmox both enabled, a Proxmox-wide failure is one failed provider, and the surviving
        // vSphere hosts still report success. Non-zero here means the Files tab will show the file as
        // missing on that provider until it is re-uploaded.
        public int FailedProviderCount { get; set; }

        // Total number of enabled providers targeted by the operation.
        public int TotalProviderCount { get; set; }

        // The hypervisor types the operation failed on, in provider registration order - the same names
        // Message reads out. Broader than FailedProviderCount: a provider that failed on only some of
        // its hosts appears here but is not counted there, because it did not fail outright.
        public List<VmType> FailedProviders { get; set; } = new();
    }
}
