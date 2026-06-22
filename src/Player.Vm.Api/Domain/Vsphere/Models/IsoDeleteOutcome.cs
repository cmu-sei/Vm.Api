// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    // Result of a datastore ISO delete across all targeted hosts. Carries only counts - the
    // per-host failure detail (host addresses/reasons) is logged server-side and deliberately
    // not surfaced to callers so it cannot leak to app users.
    public class IsoDeleteOutcome
    {
        public int FailedHostCount { get; set; }
        public int TotalHostCount { get; set; }
    }
}
