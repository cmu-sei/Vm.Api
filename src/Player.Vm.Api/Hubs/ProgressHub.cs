// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Vm.Api.Hubs
{
    [Authorize(AuthenticationSchemes = "Bearer")]
    public class ProgressHub : Hub
    {
        public ProgressHub()
        {
        }

        public async Task Join(string vmString)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, vmString);
        }

        public async Task Leave(string vmString)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, vmString);
        }
    }
}
