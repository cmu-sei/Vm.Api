// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options
{
    public class DatabaseOptions
    {
        public bool AutoMigrate { get; set; }
        public bool DevModeRecreate { get; set; }
        public string Provider { get; set; }

        public string SeedTemplateKey { get; set; }
    }
}
