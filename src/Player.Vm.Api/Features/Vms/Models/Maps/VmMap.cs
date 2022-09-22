// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;

namespace Player.Vm.Api.Features.Vms
{
    public class VmMap
    {
        public Guid Id { get; set; }
        public Guid ViewId { get; set; }
        public IEnumerable<Coordinate> Coordinates { get; set; }
        public string Name { get; set; }
        public string ImageUrl { get; set; }
        public List<Guid> TeamIds { get; set; }
    }
}