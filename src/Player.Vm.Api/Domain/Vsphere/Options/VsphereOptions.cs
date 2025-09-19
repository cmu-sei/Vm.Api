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
