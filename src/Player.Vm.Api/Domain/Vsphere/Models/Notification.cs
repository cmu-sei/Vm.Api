// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Player.Vm.Api.Domain.Vsphere.Models
{
    public class Notification
    {
        public string taskId { get; set; }
        public string taskName { get; set; }
        public string taskType { get; set; }
        public string broadcastTime { get; set; }
        public string progress { get; set; }
        public string state { get; set; }
    }
}
