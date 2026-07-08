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

        /// <summary>
        /// Human-readable rendering of the result. Returns the guest process Output when present,
        /// falling back to the Error so a stringified result is never an opaque type name.
        /// </summary>
        public override string ToString() =>
            !string.IsNullOrEmpty(Output) ? Output
            : !string.IsNullOrEmpty(Error) ? Error
            : string.Empty;
    }
}
