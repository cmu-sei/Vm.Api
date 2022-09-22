// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Player.Vm.Api.Features.Vms
{
    public class VmUserTeam
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public VmUser[] Users { get; set; }

        public VmUserTeam(Guid id, string name, VmUser[] users)
        {
            Id = id;
            Name = name;
            Users = users;
        }
    }
}
