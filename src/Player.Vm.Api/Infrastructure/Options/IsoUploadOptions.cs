// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options
{
    /// <summary>
    /// The shared ISO upload pipeline: the limits and staging every provider's write goes through.
    /// Where the bytes end up is per-provider and lives under that provider's own section -
    /// Vsphere:IsoUploadViaApi / Vsphere:IsoRoot and Proxmox:IsoUploadViaApi / Proxmox:IsoRoot.
    /// </summary>
    public class IsoUploadOptions
    {
        public long MaxFileSize { get; set; }

        /// <summary>
        /// Local directory used to stage a built ISO before it is written to its destination.
        /// When null/empty, the system temp path is used. Only used by the write modes that need a
        /// re-readable file (see IIsoProvider.RequiresStagedFile).
        /// </summary>
        public string TempStagingPath { get; set; }

        /// <summary>
        /// Timeout in minutes for an ISO upload that goes through a hypervisor's own API. Default 60.
        /// </summary>
        public int UploadTimeoutMinutes { get; set; } = 60;
    }
}
