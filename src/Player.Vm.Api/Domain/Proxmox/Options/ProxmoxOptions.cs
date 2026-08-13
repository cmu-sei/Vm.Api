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
        public bool ValidateCertificate { get; set; } = true;

        public int StateRefreshIntervalSeconds { get; set; }

        // How often ProxmoxTaskService polls cluster tasks while nothing is pending, and while at
        // least one task is still running. Mirrors the equivalent VsphereOptions settings.
        public int CheckTaskProgressIntervalMilliseconds { get; set; } = 5000;
        public int ReCheckTaskProgressIntervalMilliseconds { get; set; } = 1000;

        // Maximum payload accepted by UploadFileToGuest. The QEMU guest agent's file-write is limited
        // (~64 KiB per call), so this defaults to 60 KiB to stay safely under that ceiling.
        public int FileUploadMaxBytes { get; set; } = 61440;

        // Interval, in milliseconds, between guest-agent exec-status polls while waiting for a
        // RunGuestProcess command to finish.
        public int GuestProcessPollMs { get; set; } = 500;

        public int GuestProcessDefaultTimeoutSeconds { get; set; } = 300;

        // PVE storage id (as it appears in "storage:iso/name.iso") that holds View/team ISOs. Required
        // in BOTH write modes: even when the bytes arrive over NFS, the mount volid names the storage.
        // Empty is the ISO opt-out - Proxmox ISO support stays off.
        public string IsoStorage { get; set; }

        // True to push ISOs through PVE's own storage upload API; null or false (default) to write
        // them into IsoRoot. The vSphere equivalent is Vsphere:UploadViaApi, so a mixed deployment can
        // pair, say, vSphere-over-NFS with Proxmox-over-API.
        //
        // Nullable for the same reason as Vsphere:UploadViaApi: a blank value has to read as unset
        // rather than throwing in the configuration binder.
        public bool? UploadViaApi { get; set; }

        // Local path that is a mount of IsoStorage's template/iso directory. Required when
        // UploadViaApi is false. No rescan is needed after writing here - PVE re-reads the directory
        // the next time its content index is queried.
        public string IsoRoot { get; set; }

        // Separator folded into the filename to carry View/team scope, since Proxmox ISO storage is
        // flat and '/' is not a legal filename character (see ProxmoxIsoNaming). Configurable, not a
        // constant, so a change in how PVE normalizes uploaded filenames is a config fix.
        //
        // Must consist of [-a-zA-Z0-9_.]: PVE's storage upload API rewrites everything else to '_',
        // so a separator outside that set survives an IsoRoot write but not an API one.
        public string IsoScopeSeparator { get; set; } = "__";
    }
}
