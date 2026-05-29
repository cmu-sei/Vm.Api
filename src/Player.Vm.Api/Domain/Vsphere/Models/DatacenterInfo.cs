// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using VimClient;

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public class DatacenterInfo
    {
        // Inventory name of the datacenter, used for the dcPath query param on datastore HTTP uploads.
        public string Name { get; set; }

        // Datacenter MoRef, used when creating directories on the datastore via FileManager.
        public ManagedObjectReference Reference { get; set; }
    }
}
