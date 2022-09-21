// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;

namespace Player.Vm.Api.Domain.Models
{
    public class ConsoleConnectionInfo
    {
        public string Hostname { get; set; }
        public string Port { get; set; }
        public string Protocol { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
