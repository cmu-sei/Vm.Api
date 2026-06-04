// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public class GuestProcessResult
    {
        public string Output { get; set; }
        public int ExitCode { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
