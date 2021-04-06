// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Extensions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Player.Api.Client;

namespace Player.Vm.Api.Features.Vms.Hubs
{
    [Authorize(AuthenticationSchemes = "Bearer")]
    public class VmHub : Hub
    {
        private readonly IPlayerService _playerService;
        private readonly IActiveVirtualMachineService _activeVirtualMachineService;
        private readonly IVmService _vmService;
        private readonly IViewService _viewService;
        private const string UserGroupPrefix = "ActiveConsoles";

        public VmHub(IPlayerService playerService,
                     IActiveVirtualMachineService activeVirtualMachineService,
                     IVmService vmService,
                     IViewService viewService)
        {
            _playerService = playerService;
            _activeVirtualMachineService = activeVirtualMachineService;
            _vmService = vmService;
            _viewService = viewService;
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

        public async Task<IEnumerable<VmUserTeam>> JoinViewUsers(Guid viewId)
        {
            var vmUserTeams = new List<VmUserTeam>();
            var groupId = await _playerService.GetGroupIdForViewAsync(viewId, Context.ConnectionAborted);

            if (!groupId.HasValue)
            {
                return vmUserTeams.ToArray();
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GetGroup(groupId.Value));

            var teams = await _playerService.GetTeamsByViewIdAsync(viewId, Context.ConnectionAborted);

            var teamTaskDict = new Dictionary<Guid, Task<IEnumerable<User>>>();

            foreach (var team in teams)
            {
                var task = _playerService.GetUsersByTeamId(team.Id, Context.ConnectionAborted);
                teamTaskDict.Add(team.Id, task);
            }

            await Task.WhenAll(teamTaskDict.Values);

            foreach (var kvp in teamTaskDict)
            {
                var vmUsers = new List<VmUser>();
                var teamId = kvp.Key;
                var team = teams.FirstOrDefault(t => t.Id == teamId);
                var users = kvp.Value.Result;

                foreach (var user in users)
                {
                    Guid? activeVmId = null;
                    var activeVm = _activeVirtualMachineService.GetActiveVirtualMachineForUser(user.Id);

                    if (activeVm != null && activeVm.TeamIds.Contains(teamId))
                    {
                        activeVmId = activeVm.VmId;
                    }

                    vmUsers.Add(new VmUser(user.Id, user.Name, activeVmId));
                }

                vmUserTeams.Add(new VmUserTeam(teamId, team.Name, vmUsers.ToArray()));
            }

            return vmUserTeams;
        }

        public async Task LeaveViewUsers(Guid viewId)
        {
            var groupId = await _playerService.GetGroupIdForViewAsync(viewId, Context.ConnectionAborted);

            if (groupId.HasValue)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroup(groupId.Value));
            }
        }

        public async Task<VmUser> JoinUser(Guid userId, Guid viewId)
        {
            var activeVm = _activeVirtualMachineService.GetActiveVirtualMachineForUser(userId);
            Guid? activeVmId = null;

            var groupId = await _playerService.GetGroupIdForViewAsync(viewId, Context.ConnectionAborted);

            if (groupId.HasValue)
            {
                if (groupId == viewId)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, GetGroup(viewId, userId));
                    activeVmId = activeVm?.VmId;
                }
                else
                {
                    // Check if this user's team is allowed to see the target user's activeVm
                    if (activeVm != null && activeVm.TeamIds.Contains(groupId.Value))
                    {
                        activeVmId = activeVm.VmId;
                    }

                    await Groups.AddToGroupAsync(Context.ConnectionId, GetGroup(groupId.Value, userId));
                }
            }

            var user = await _playerService.GetUserById(userId, Context.ConnectionAborted);

            return new VmUser(userId, user.Name, activeVmId);
        }

        public async Task LeaveUser(Guid userId, Guid viewId)
        {
            var groupId = await _playerService.GetGroupIdForViewAsync(viewId, Context.ConnectionAborted);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroup(groupId.Value, userId));

        }

        public async Task SetActiveVirtualMachine(Guid vmId)
        {
            var vm = await _vmService.GetAsync(vmId, Context.ConnectionAborted);
            var userId = Context.User.GetId();

            var viewIds = await _viewService.GetViewIdsForTeams(vm.TeamIds, Context.ConnectionAborted);

            var teamIds = new List<Guid>();

            foreach (var viewId in viewIds)
            {
                var primaryTeamId = await _playerService.GetPrimaryTeamByViewIdAsync(viewId, Context.ConnectionAborted);
                teamIds.Add(primaryTeamId);
            }

            var groups = GetGroups(teamIds, viewIds, userId);

            var newVmId = _activeVirtualMachineService.SetActiveVirtualMachineForUser(userId, vmId, Context.ConnectionId, teamIds);

            await Clients.Groups(groups).SendAsync(VmHubMethods.ActiveVirtualMachine, newVmId, userId);
        }

        public async Task UnsetActiveVirtualMachine()
        {
            await UnsetActiveVirtualMachineInternal();
        }

        public override async Task OnDisconnectedAsync(Exception ex)
        {
            await UnsetActiveVirtualMachineInternal();
            await base.OnDisconnectedAsync(ex);
        }

        private async Task UnsetActiveVirtualMachineInternal()
        {
            var userId = Context.User.GetId();
            var activeVirtualMachine = _activeVirtualMachineService.UnsetActiveVirtualMachineForUser(userId, Context.ConnectionId);

            if (activeVirtualMachine != null)
            {
                var viewIds = await _viewService.GetViewIdsForTeams(activeVirtualMachine.TeamIds, Context.ConnectionAborted);
                var groups = GetGroups(activeVirtualMachine.TeamIds, viewIds, userId);

                await Clients.Groups(groups).SendAsync(VmHubMethods.ActiveVirtualMachine, null, userId);
            }
        }

        private string[] GetGroups(IEnumerable<Guid> teamIds, IEnumerable<Guid> viewIds, Guid userId)
        {
            var groups = new List<string>();

            foreach (var id in teamIds.Concat(viewIds))
            {
                // those following this user who have ViewAdmin
                // those following this user who are on the same Team
                groups.Add(GetGroup(id, userId));

                // those following the entire view who have ViewAdmin
                // those following the entire view who are on the same Team
                groups.Add(GetGroup(id));
            }

            return groups.ToArray();
        }

        private string GetGroup(Guid groupId)
        {
            return GetGroup(groupId, null);
        }

        /// <summary>
        /// Get the signalR group for the given parameters
        /// </summary>
        /// <param name="groupId"></param>
        /// <param name="userId"></param>
        /// <returns>a string in the form of {UserGroupPrefix}-{userId}-{groupId}, omitting -{userId} if null</returns>
        private string GetGroup(Guid groupId, Guid? userId)
        {
            var group = new StringBuilder(UserGroupPrefix);

            if (userId.HasValue)
            {
                group.Append($"-{userId}");
            }

            group.Append($"-{groupId}");

            return group.ToString();
        }
    }

    public static class VmHubMethods
    {
        public const string VmCreated = "VmCreated";
        public const string VmUpdated = "VmUpdated";
        public const string VmDeleted = "VmDeleted";
        public const string ActiveVirtualMachine = "ActiveVirtualMachine";
    }
}
