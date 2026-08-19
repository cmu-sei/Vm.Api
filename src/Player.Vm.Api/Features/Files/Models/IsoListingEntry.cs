// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Features.Files.Models
{
    // One ISO as a provider reports it: the display name, and the exact token that provider's mount
    // command expects for it. vSphere: the datastore path. Proxmox: the volume id verbatim as PVE
    // reported it, which keeps it correct even if PVE normalized the name on upload.
    //
    // Never serialized: no controller returns it, so it reaches no OpenAPI schema. Both response
    // shapes are projected from it - a VM-scoped listing to MountableIsoFile, and the merged
    // management listing to ManagedIsoFile, which drops the token because a file on several providers
    // at once has no single one. Public only because IIsoProvider is (an internal type cannot appear
    // on a public interface, nor in the public constructor DI has to activate IsoService through).
    public sealed record IsoListingEntry(string Filename, string MountValue);
}
