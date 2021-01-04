// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Vms
{
    public class ConsoleConnectionInfo
    {
        /// <summary>
        /// The hostname or address to use to connect
        /// </summary>
        public string Hostname { get; set; }

        /// <summary>
        /// The port to use to connect
        /// </summary>
        public string Port { get; set; }

        /// <summary>
        /// The protocol to use to connect, such as rdp, ssh, etc
        /// </summary>
        public string Protocol { get; set; }

        /// <summary>
        /// The optional username to use to connect
        /// If omitted, the user will be prompted on connection
        /// Note: This must be set to connect to a Windows machine using Network Level Authentication
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// The optional password to use to connect
        /// If omitted, the user will be prompted on connection
        /// Note: This must be set to connect to a Windows machine using Network Level Authentication
        /// </summary>
        public string Password { get; set; }
    }
}
