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
    }
}
