// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Player.Vm.Api.Features.Vms
{
    public class VmUser
    {
        /// <summary>
        /// User's unique Id
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// User's name
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Id of the Vm this User is currently viewing, if any
        /// </summary>
        public Guid? ActiveVmId { get; set; }

        public Guid? LastVmId { get; set; }
        public DateTimeOffset? LastSeen { get; set; }

        public VmUser(Guid userId, string username, Guid? activeVmId, Guid? lastVmId, DateTimeOffset? lastSeen)
        {
            UserId = userId;
            Username = username;
            ActiveVmId = activeVmId;
            LastVmId = lastVmId;
            LastSeen = lastSeen;
        }
    }
}
