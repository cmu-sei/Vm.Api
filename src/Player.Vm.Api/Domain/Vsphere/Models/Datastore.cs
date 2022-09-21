// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using VimClient;

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public class Datastore
    {
        public string Name { get; set; }
        public ManagedObjectReference Reference { get; set; }
        public ManagedObjectReference Browser { get; set; }
    }
}
