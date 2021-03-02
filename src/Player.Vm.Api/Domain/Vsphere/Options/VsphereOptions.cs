// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Vsphere.Options
{
    public class VsphereOptions
    {
        public string Host { get; set; }

        public string Username { get; set; }

        public string Password { get; set; }

        public int ConnectionRetryIntervalSeconds { get; set; }
        public int ConnectionRefreshIntervalMinutes { get; set; }
        public int LoadCacheAfterIterations { get; set; }
        public bool LogConsoleAccess { get; set; }

        public string DsName { get; set; }
        public string BaseFolder { get; set; }
        public int Timeout { get; set; }
        public int CheckTaskProgressIntervalMilliseconds { get; set; }
        public int ReCheckTaskProgressIntervalMilliseconds { get; set; }
        public int HealthAllowanceSeconds { get; set; }
    }
}
