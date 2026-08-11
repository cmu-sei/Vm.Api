// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Player.Vm.Api.Features.Files.Models;

namespace Player.Vm.Api.Features.Files
{
    public class IsoResult
    {
        public Guid ViewId { get; set; }
        public string ViewName { get; set; }
        public IsoFile[] Isos { get; set; }
        public List<TeamIsoResult> TeamIsoResults { get; set; } = new List<TeamIsoResult>();
    }

    public class TeamIsoResult
    {
        public Guid TeamId { get; set; }
        public string TeamName { get; set; }
        public IsoFile[] Isos { get; set; }
    }
}
