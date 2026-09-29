// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Features.Files.Models
{
    // Result of a datastore ISO operation (upload or delete) across all targeted destinations. Carries only
    // counts (one per vSphere storage group per scope) - attempt details are logged server-side and
    // deliberately not surfaced to callers so it cannot leak to app users.
    public class IsoOperationOutcome
    {
        public int FailedHostCount { get; set; }
        public int TotalHostCount { get; set; }
    }
}
