// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Services;

namespace Player.Vm.Api.Hubs
{
    [Authorize(AuthenticationSchemes = "Bearer")]
    public class VmHub : Hub
    {
        private readonly IPlayerService _playerService;

        public VmHub(IPlayerService playerService)
        {
            _playerService = playerService;
        }

        public async Task JoinView(Guid viewId)
        {
            var groupId = await _playerService.GetGroupIdForViewAsync(viewId, Context.ConnectionAborted);

            if (groupId.HasValue)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, groupId.ToString());
            }
        }

        public async Task LeaveView(Guid viewId)
        {
            var groupId = await _playerService.GetGroupIdForViewAsync(viewId, Context.ConnectionAborted);

            if (groupId.HasValue)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupId.ToString());
            }
        }
    }

    public static class VmHubMethods
    {
        public const string VmCreated = "VmCreated";
        public const string VmUpdated = "VmUpdated";
        public const string VmDeleted = "VmDeleted";
    }
}
