// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options
{
    public class VmLoggingOptions
    {
        public bool Enabled { get; set; }
        public string PostgreSQL { get; set; }
    }
}
