// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Runtime.Serialization;

namespace Player.Vm.Api.Features.Files
{
    [DataContract(Name = "IsoUploadResult")]
    public class IsoUploadResult
    {
        // Generic, admin-safe summary of the upload outcome. Never contains host identifiers.
        public string Message { get; set; }

        // Number of hosts the ISO failed to upload to (0 when fully successful). Counts only -
        // which hosts failed is recorded in server logs for admins, not exposed to app users.
        public int FailedHostCount { get; set; }

        // Total number of hosts targeted by the upload.
        public int TotalHostCount { get; set; }
    }
}
