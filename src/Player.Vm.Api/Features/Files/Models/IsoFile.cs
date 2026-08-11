// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Files.Models
{
    public class IsoFile
    {
        public IsoFile(string path, string filename)
        {
            this.Path = path;
            this.Filename = filename;
        }

        // vSphere's host-specific "[dsName] baseFolder/viewId/scopeId/" folder path, with VMware's
        // trailing slash. Null for providers with no folder hierarchy (Proxmox), and null on a merged
        // management listing where the file exists on several providers at once. Kept populated for
        // vSphere so a client that still concatenates path + filename keeps working; new clients
        // should use MountValue.
        public string Path { get; set; }

        public string Filename { get; set; }

        // The exact token this provider's mount command expects for this file, computed server-side so
        // clients stop assembling it. vSphere: the datastore path. Proxmox: the volume id verbatim as
        // PVE reported it, which keeps it correct even if PVE normalized the name on upload.
        //
        // Null on the management listing (Files tab), which never mounts - mount pickers get their
        // rows from a VM-scoped listing where exactly one provider answered.
        public string MountValue { get; set; }

        public VmType? ProviderType { get; set; }

        public string ProviderInstanceId { get; set; }

        // Management listing only: enabled providers that do NOT have this file. Non-empty means an
        // earlier upload only partly succeeded; re-uploading the same name heals it.
        public List<VmType> MissingProviders { get; set; } = new();
    }
}
