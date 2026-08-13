// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Player.Vm.Api.Features.Files.Models;

namespace Player.Vm.Api.Features.Files
{
    // The view-wide + per-team ISO listing, in two shapes for the two workflows. Deliberately two
    // concrete pairs rather than one generic pair, so the generated OpenAPI schema names stay
    // readable in the clients.

    // Management listing (the Files tab): merged across providers, never mounted.
    public class ManagedIsoResult
    {
        public Guid ViewId { get; set; }
        public string ViewName { get; set; }
        public ManagedIsoFile[] Isos { get; set; }
        public List<ManagedTeamIsoResult> TeamIsoResults { get; set; } = new List<ManagedTeamIsoResult>();
    }

    public class ManagedTeamIsoResult
    {
        public Guid TeamId { get; set; }
        public string TeamName { get; set; }
        public ManagedIsoFile[] Isos { get; set; }
    }

    // VM-scoped listing: one provider, and every row carries the token its mount command takes.
    public class MountableIsoResult
    {
        public Guid ViewId { get; set; }
        public string ViewName { get; set; }
        public MountableIsoFile[] Isos { get; set; }
        public List<MountableTeamIsoResult> TeamIsoResults { get; set; } = new List<MountableTeamIsoResult>();
    }

    public class MountableTeamIsoResult
    {
        public Guid TeamId { get; set; }
        public string TeamName { get; set; }
        public MountableIsoFile[] Isos { get; set; }
    }
}
