// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options
{
    public class IsoUploadOptions
    {
        public string BasePath { get; set; }
        public long MaxFileSize { get; set; }
    }
}
