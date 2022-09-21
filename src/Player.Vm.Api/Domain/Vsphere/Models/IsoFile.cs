// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public class IsoFile
    {
        public IsoFile(string path, string filename)
        {
            this.Path = path;
            this.Filename = filename;
        }

        public string Path { get; set; }
        public string Filename { get; set; }
    }
}