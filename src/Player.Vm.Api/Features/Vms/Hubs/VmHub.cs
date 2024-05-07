// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
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
using Player.Vm.Api.Data;
using Player.Vm.Api.Infrastructure.Options;
using System.Threading;

namespace Player.Vm.Api.Features.Vms.Hubs
{
    public class VmHub : Hub
    {
        private readonly IPlayerService _playerService;
        private readonly IActiveVirtualMachineService _activeVirtualMachineService;
        private readonly IVmService _vmService;
        private readonly IViewService _viewService;
        private readonly IVmUsageLoggingService _vmUsageLoggingService;
        private const string UserGroupPrefix = "ActiveConsoles";

        public VmHub(
            IActiveVirtualMachineService activeVirtualMachineService,
            IVmUsageLoggingService vmUsageLoggingService,
            IViewService viewService,
            IPlayerService playerService,
            IVmService vmService)
        {
            _activeVirtualMachineService = activeVirtualMachineService;
            _vmUsageLoggingService = vmUsageLoggingService;
            _viewService = viewService;
            _playerService = playerService;
            _vmService = vmService;
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

        public async Task JoinVm(Guid vmId)
        {
            var vm = await _vmService.GetAsync(vmId, Context.ConnectionAborted);
            var viewIds = await _viewService.GetViewIdsForTeams(vm.TeamIds, Context.ConnectionAborted);

            var teams = new List<Team>();

            foreach (var viewId in viewIds)
            {
                var primaryTeam = await _playerService.GetPrimaryTeamByViewIdAsync(viewId, Context.ConnectionAborted);
                teams.Add(primaryTeam);
            }

            foreach (var team in teams)
            {
                Guid groupId;

                if (team.CanManage)
                {
                    groupId = team.ViewId;
                }
                else
                {
                    groupId = team.Id;
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, GetCurrentVmUsersChannelName(groupId, vmId));
            }
        }

        public async Task LeaveVm(Guid vmId)
        {
            var vm = await _vmService.GetAsync(vmId, Context.ConnectionAborted);
            var viewIds = await _viewService.GetViewIdsForTeams(vm.TeamIds, Context.ConnectionAborted);

            var teams = new List<Team>();

            foreach (var viewId in viewIds)
            {
                var primaryTeam = await _playerService.GetPrimaryTeamByViewIdAsync(viewId, new System.Threading.CancellationToken());
                teams.Add(primaryTeam);
            }

            foreach (var team in teams)
            {
                Guid groupId;

                if (team.CanManage)
                {
                    groupId = team.ViewId;
                }
                else
                {
                    groupId = team.Id;
                }

                await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetCurrentVmUsersChannelName(groupId, vmId));
            }
        }


        public async Task SetActiveVirtualMachine(Guid vmId)
        {
            var vm = await _vmService.GetAsync(vmId, Context.ConnectionAborted);
            var userId = Context.User.GetId();

            var viewIds = await _viewService.GetViewIdsForTeams(vm.TeamIds, Context.ConnectionAborted);

            var teams = new List<Team>();

            foreach (var viewId in viewIds)
            {
                var primaryTeam = await _playerService.GetPrimaryTeamByViewIdAsync(viewId, Context.ConnectionAborted);
                teams.Add(primaryTeam);
            }

            var teamIds = teams.Select(x => x.Id);
            var groups = GetGroups(teams.Select(x => x.Id), viewIds, userId, vmId);

            var newVmId = _activeVirtualMachineService.SetActiveVirtualMachineForUser(userId, Context.User.GetName(), vmId, Context.ConnectionId, teamIds);

            await Clients.Groups(groups).SendAsync(VmHubMethods.ActiveVirtualMachine, newVmId, userId);

            // Begin Handling of displaying current users connected to an individual VM
            var userNamesByGroup = await _activeVirtualMachineService.GetActiveVirtualMachineUsersByGroup(vmId, null, Context.ConnectionAborted);

            foreach (var kvp in userNamesByGroup)
            {
                await Clients.Groups(GetCurrentVmUsersChannelName(kvp.Key, vmId)).SendAsync(VmHubMethods.CurrentVirtualMachineUsers, vmId, kvp.Value);
            }

            await _vmUsageLoggingService.CreateVmLogEntry(userId, vmId, teamIds, Context.ConnectionAborted);
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
            var cancellationToken = new CancellationToken(); // This gets called on disconnect, so can't use Context.ConnectionAborted because it will always be true
            var userId = Context.User.GetId();
            var activeVirtualMachine = _activeVirtualMachineService.UnsetActiveVirtualMachineForUser(userId, Context.User.GetName(), Context.ConnectionId);

            if (activeVirtualMachine != null)
            {
                var viewIds = await _viewService.GetViewIdsForTeams(activeVirtualMachine.TeamIds, cancellationToken);
                var groups = GetGroups(activeVirtualMachine.TeamIds, viewIds, userId, activeVirtualMachine.VmId);
                await Clients.Groups(groups).SendAsync(VmHubMethods.ActiveVirtualMachine, null, userId);

                // Begin Handling of displaying current users connected to an individual VM
                var userNamesByGroup = await _activeVirtualMachineService.GetActiveVirtualMachineUsersByGroup(activeVirtualMachine.VmId, activeVirtualMachine, cancellationToken);

                foreach (var kvp in userNamesByGroup)
                {
                    await Clients.Groups(GetCurrentVmUsersChannelName(kvp.Key, activeVirtualMachine.VmId)).SendAsync(VmHubMethods.CurrentVirtualMachineUsers, activeVirtualMachine.VmId, kvp.Value);
                }

                await _vmUsageLoggingService.CloseVmLogEntry(userId, activeVirtualMachine.VmId, cancellationToken);
            }
        }

        private string[] GetGroups(IEnumerable<Guid> teamIds, IEnumerable<Guid> viewIds, Guid userId, Guid vmId)
        {
            var groups = new List<string>();

            foreach (var id in teamIds.Concat(viewIds))
            {
                // those following this user who have ViewAdmin
                // those following this user who are on the same Team
                groups.Add(GetGroup(id, userId));
                groups.Add(GetCurrentVmUsersChannelName(id, vmId));

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

        /// <summary>
        /// Get the signalR channel name for the given vm user
        /// </summary>
        /// <param name="groupId"></param>
        /// <param name="vmId"></param>
        /// <returns>a string in the form of {VmUserChannelPrefix}-{vmId}</returns>
        private string GetCurrentVmUsersChannelName(Guid groupId, Guid vmId)
        {
            var channelName = new StringBuilder(VmHubMethods.CurrentVirtualMachineUsers);

            channelName.Append($"-{groupId}");
            channelName.Append($"-{vmId}");

            return channelName.ToString();
        }
    }

    public static class VmHubMethods
    {
        public const string VmCreated = "VmCreated";
        public const string VmUpdated = "VmUpdated";
        public const string VmDeleted = "VmDeleted";
        public const string ActiveVirtualMachine = "ActiveVirtualMachine";
        public const string CurrentVirtualMachineUsers = "CurrentVirtualMachineUsers";
    }
}
