// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Features.Files.Models
{
    // An ISO on a VM-scoped listing, which feeds the mount picker. Exactly one provider answers such
    // a listing, so MountValue is always populated: it is computed server-side and handed straight
    // back to the mount command, so no client assembles a provider-specific path.
    public class MountableIsoFile
    {
        public MountableIsoFile(string filename, string mountValue)
        {
            this.Filename = filename;
            this.MountValue = mountValue;
        }

        public string Filename { get; set; }

        public string MountValue { get; set; }
    }
}
