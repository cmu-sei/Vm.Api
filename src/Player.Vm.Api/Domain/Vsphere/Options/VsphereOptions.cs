// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Vsphere.Options
{
    public class VsphereOptions
    {
        public int ConnectionRetryIntervalSeconds { get; set; }
        public int ConnectionRefreshIntervalMinutes { get; set; }
        public int LoadCacheAfterIterations { get; set; }
        public int LoadCacheAfterMinutes { get; set; }
        public int ConnectionTimeoutSeconds { get; set; }
        public bool LogConsoleAccess { get; set; }
        public int CheckTaskProgressIntervalMilliseconds { get; set; }
        public int ReCheckTaskProgressIntervalMilliseconds { get; set; }
        public int HealthAllowanceSeconds { get; set; }
        public bool SkipGuestFileCertificateValidation { get; set; } = false;
        public int GuestFileTransferTimeoutMinutes { get; set; } = 3;
        public int TaskTimeoutMinutes { get; set; } = 10;
        public int TaskInfoUnavailableTimeoutSeconds { get; set; } = 30;
        public string GuestProcessTempPath { get; set; }
        public int GuestProcessDefaultTimeoutSeconds { get; set; } = 300;

        // How long to wait between polls while a synchronously-awaited vCenter task or guest process
        // runs (VsphereService.WaitForVimTask, RunGuestProcess). Distinct from
        // CheckTaskProgressIntervalMilliseconds, which paces the background TaskService poller.
        // Exists mainly so tests can set it to 0 and exercise those loops without real waits.
        public int TaskPollIntervalMilliseconds { get; set; } = 1000;

        // True to push ISOs to every connected vCenter's datastore through its HTTP file API; null or
        // false (default) to write them into IsoRoot. Required by VMware Cloud on AWS SDDCs, which
        // have no NFS datastore. The Proxmox equivalent is Proxmox:IsoUploadViaApi.
        //
        // Each host is written through its OWN DsName and BaseFolder, so an ISO lands at
        // "[{DsName}] {BaseFolder}/{viewId}/{scopeId}/{filename}" (see
        // VsphereService.BuildIsoFolderRelative, the single source of truth for that layout). The
        // destination folder is created if it does not exist.
        //
        // Nullable so that a blank value reads as unset: an environment-variable deployment cannot
        // remove a key, only blank it, and the configuration binder throws converting "" to bool -
        // which, bound from a background service, stops the host.
        public bool? IsoUploadViaApi { get; set; }

        // Local path, on a share the hosts also mount, holding the {viewId}/{scopeId} ISO folders.
        // Required when IsoUploadViaApi is false.
        //
        // Must be a mount whose contents surface at exactly "[{DsName}] {BaseFolder}": listing and the
        // VM mount picker browse the datastore in BOTH write modes, so a share that is a datastore but
        // sits at some other path within it writes ISOs no one can then see.
        public string IsoRoot { get; set; }

        public VsphereHost[] Hosts { get; set; }
    }

    public class VsphereHost
    {
        public bool Enabled { get; set; } = true;
        public string Address { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DsName { get; set; }
        public string BaseFolder { get; set; }
    }
}
