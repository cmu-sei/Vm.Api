// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Player.Vm.Api.Features.Vms
{
    public class SimpleTeam
    {
        public Guid Id { get; set; }
        public string Name { get; set; }

        public SimpleTeam(Guid id, string name)
        {
            this.Id = id;
            this.Name = name;
        }
    }
}
