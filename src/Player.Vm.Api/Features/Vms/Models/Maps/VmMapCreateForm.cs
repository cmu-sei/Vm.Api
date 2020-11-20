// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;

namespace Player.Vm.Api.Features.Vms
{
    public class VmMapCreateForm
    {
        public IEnumerable<CoordinateCreateForm> Coordinates { get; set; }
        public string Name { get; set; }
        public string ImageUrl { get; set; }
        public List<Guid> TeamIds { get; set; }
        public Guid? Id { get; set; }
    }
}