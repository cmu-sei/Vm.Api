// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Files.Models
{
    // An ISO on the management listing (the Files tab), merged across every enabled provider. It
    // carries no mount token on purpose: with the file possibly on several providers there is no
    // single one, and this listing never mounts - a mount picker gets its rows from a VM-scoped
    // listing, where exactly one provider answered.
    public class ManagedIsoFile
    {
        public ManagedIsoFile(string filename)
        {
            this.Filename = filename;
        }

        public string Filename { get; set; }

        // Enabled providers that do NOT have this file. Non-empty means an earlier upload only partly
        // succeeded; re-uploading the same name heals it.
        public List<VmType> MissingProviders { get; set; } = new();
    }
}
