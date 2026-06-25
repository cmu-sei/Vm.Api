// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    // Shared ISO filename conventions so upload staging, datastore enumeration, and NFS naming all
    // agree on what counts as an ISO and how the extension is spelled.
    public static class IsoFileNaming
    {
        public const string Extension = ".iso";

        // True when the filename already carries the ISO extension (case-insensitive).
        public static bool IsIsoFile(string filename)
        {
            return Path.GetExtension(filename).Equals(Extension, StringComparison.OrdinalIgnoreCase);
        }
    }
}
