// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Extensions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Player.Api.Client;
using Player.Vm.Api.Data;
using System.Threading;
using Microsoft.EntityFrameworkCore;

namespace Player.Vm.Api.Features.Vms.Hubs
{
    public class VmHub : Hub
    {
        private readonly IPlayerService _playerService;
        private readonly IActiveVirtualMachineService _activeVirtualMachineService;
        private readonly IVmService _vmService;
        private readonly IViewService _viewService;
        private readonly IVmUsageLoggingService _vmUsageLoggingService;
        private readonly IXApiService _xApiService;
        private readonly VmContext _dbContext;
        private const string UserGroupPrefix = "ActiveConsoles";

        public VmHub(
            IActiveVirtualMachineService activeVirtualMachineService,
            IVmUsageLoggingService vmUsageLoggingService,
            IViewService viewService,
            IPlayerService playerService,
            IVmService vmService,
            IXApiService xApiService,
            VmContext dbContext)
        {
            _activeVirtualMachineService = activeVirtualMachineService;
            _vmUsageLoggingService = vmUsageLoggingService;
            _viewService = viewService;
            _playerService = playerService;
            _vmService = vmService;
            _xApiService = xApiService;
            _dbContext = dbContext;
        }

        public async Task JoinView(Guid viewId)
        {
            var groupIds = await _playerService.GetGroupIdsForViewAsync(viewId, Context.ConnectionAborted);
            foreach (var groupId in groupIds)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, groupId.ToString());
            }
        }

        public async Task LeaveView(Guid viewId)
        {
            var groupIds = await _playerService.GetGroupIdsForViewAsync(viewId, Context.ConnectionAborted);
            foreach (var groupId in groupIds)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupId.ToString());
            }
        }

        public async Task<IEnumerable<VmUserTeam>> JoinViewUsers(Guid viewId)
        {
            var vmUserTeams = new List<VmUserTeam>();
            var groupIds = await _playerService.GetGroupIdsForViewAsync(viewId, Context.ConnectionAborted);

            if (!groupIds.Any())
            {
                return vmUserTeams;
            }

            foreach (var groupId in groupIds)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GetGroup(groupId));
            }

            var teams = await _playerService.GetTeamsByViewIdAsync(viewId, Context.ConnectionAborted);
            if (teams == null)
                return vmUserTeams;

            var teamTaskDict = new Dictionary<Guid, Task<IEnumerable<User>>>();

            foreach (var team in teams)
            {
                var task = _playerService.GetUsersByTeamId(team.Id, Context.ConnectionAborted);
                teamTaskDict.Add(team.Id, task);
            }

            await Task.WhenAll(teamTaskDict.Values);

            var teamIds = teams.Select(x => x.Id);

            var dbUsers = await _dbContext.VmUsers
                .Where(x => teamIds.Contains(x.TeamId))
                .ToListAsync();

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

                    var dbUser = dbUsers
                        .Where(x => x.UserId == user.Id && x.TeamId == teamId)
                        .FirstOrDefault();

                    vmUsers.Add(new VmUser(user.Id, teamId, user.Name, activeVmId, dbUser?.LastVmId, dbUser?.LastSeen));
                }

                vmUserTeams.Add(new VmUserTeam(teamId, team.Name, vmUsers.ToArray()));
            }

            return vmUserTeams;
        }

        public async Task LeaveViewUsers(Guid viewId)
        {
            var groupIds = await _playerService.GetGroupIdsForViewAsync(viewId, Context.ConnectionAborted);
            foreach (var groupId in groupIds)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroup(groupId));
            }
        }

        public async Task<VmUser> JoinUser(Guid userId, Guid viewId, Guid teamId)
        {
            var visibility = await _playerService.GetVisibilityContextAsync(viewId, Context.ConnectionAborted);
            if (!visibility.TeamIds.Contains(teamId))
                throw new HubException("You do not have access to this team");

            var activeVm = _activeVirtualMachineService.GetActiveVirtualMachineForUser(userId);
            Guid? activeVmId = null;
            var groupId = visibility.CanViewAllTeams ? viewId : teamId;

            await Groups.AddToGroupAsync(Context.ConnectionId, GetGroup(groupId, userId));

            if (activeVm != null)
            {
                if (visibility.CanViewAllTeams)
                {
                    var activeViewIds = await _viewService.GetViewIdsForTeams(
                        activeVm.TeamIds,
                        Context.ConnectionAborted);
                    if (activeViewIds.Contains(viewId))
                        activeVmId = activeVm.VmId;
                }
                else if (activeVm.TeamIds.Contains(teamId))
                {
                    activeVmId = activeVm.VmId;
                }
            }

            var user = await _playerService.GetUserById(userId, Context.ConnectionAborted);
            await _xApiService.TrackUserFollowedAsync(
                userId,
                user.Name,
                viewId,
                teamId,
                Context.ConnectionAborted);
            var dbUser = await _dbContext.VmUsers
                .Where(x => x.UserId == userId && x.TeamId == teamId)
                .FirstOrDefaultAsync();

            return new VmUser(userId, teamId, user.Name, activeVmId, dbUser?.LastVmId, dbUser?.LastSeen);
        }

        public async Task LeaveUser(Guid userId, Guid viewId)
        {
            var user = await _playerService.GetUserById(userId, Context.ConnectionAborted);
            var groupIds = await _playerService.GetGroupIdsForViewAsync(viewId, Context.ConnectionAborted);
            foreach (var groupId in groupIds)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroup(groupId, userId));
            }

            await _xApiService.TrackUserUnfollowedAsync(
                userId,
                user.Name,
                viewId,
                Context.ConnectionAborted);
        }

        public async Task JoinVm(Guid vmId)
        {
            await SetVm(vmId, join: true);
        }

        public async Task LeaveVm(Guid vmId)
        {
            await SetVm(vmId, join: false);
        }

        private async Task SetVm(Guid vmId, bool join)
        {
            var vm = await _vmService.GetAsync(vmId, Context.ConnectionAborted);
            var viewIds = await _viewService.GetViewIdsForTeams(vm.TeamIds, Context.ConnectionAborted);
            var groupIds = new HashSet<Guid>();

            foreach (var viewId in viewIds)
            {
                var visibility = await _playerService.GetVisibilityContextAsync(viewId, Context.ConnectionAborted);
                if (!vm.TeamIds.Any(visibility.TeamIds.Contains))
                    continue;

                if (visibility.CanViewAllTeams)
                {
                    groupIds.Add(viewId);
                }
                else
                {
                    groupIds.UnionWith(visibility.TeamIds);
                }
            }

            foreach (var groupId in groupIds)
            {
                if (join)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, GetCurrentVmUsersChannelName(groupId, vmId));
                }
                else
                {
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetCurrentVmUsersChannelName(groupId, vmId));
                }
            }
        }


        public async Task SetActiveVirtualMachine(Guid vmId)
        {
            var vm = await _vmService.GetAsync(vmId, Context.ConnectionAborted);
            var userId = Context.User.GetId();

            var viewIds = await _viewService.GetViewIdsForTeams(vm.TeamIds, Context.ConnectionAborted);

            var teamIds = new HashSet<Guid>();
            var visibleViewIds = new HashSet<Guid>();

            foreach (var viewId in viewIds)
            {
                var visibility = await _playerService.GetVisibilityContextAsync(viewId, Context.ConnectionAborted);

                if (visibility.PrimaryTeamId.HasValue &&
                    vm.TeamIds.Any(visibility.TeamIds.Contains))
                {
                    teamIds.Add(visibility.PrimaryTeamId.Value);
                    visibleViewIds.Add(viewId);
                }
            }

            var groups = GetGroups(teamIds, visibleViewIds, userId, vmId);

            // Presence remains scoped to the user's primary/view context so elevated
            // VM access does not expose a non-member user to a team.
            var newVmId = await _activeVirtualMachineService.SetActiveVirtualMachineForUser(userId, Context.User.GetName(), vm, Context.ConnectionId, teamIds, Context.ConnectionAborted);

            await Clients.Groups(groups).SendAsync(VmHubMethods.ActiveVirtualMachine, newVmId, userId, DateTimeOffset.UtcNow, teamIds);

            // Begin Handling of displaying current users connected to an individual VM
            var userNamesByGroup = await _activeVirtualMachineService.GetActiveVirtualMachineUsersByGroup(vmId, null, CancellationToken.None);

            foreach (var kvp in userNamesByGroup)
            {
                await Clients.Groups(GetCurrentVmUsersChannelName(kvp.Key, vmId)).SendAsync(
                    VmHubMethods.CurrentVirtualMachineUsers,
                    vmId,
                    kvp.Value,
                    kvp.Key);
            }

            await _vmUsageLoggingService.CreateVmLogEntry(userId, vmId, teamIds, CancellationToken.None);
            await _xApiService.TrackConsoleOpenedAsync(vmId, teamIds, CancellationToken.None);
            await UpdateVmUser(userId, vmId, teamIds);
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
            var cancellationToken = CancellationToken.None; // still update other users if this connection disconnects
            var userId = Context.User.GetId();
            var activeVirtualMachine = await _activeVirtualMachineService.UnsetActiveVirtualMachineForUser(userId, Context.User.GetName(), Context.ConnectionId, Context.ConnectionAborted);

            if (activeVirtualMachine != null)
            {
                var viewIds = await _viewService.GetViewIdsForTeams(activeVirtualMachine.TeamIds, cancellationToken);

                var groups = GetGroups(activeVirtualMachine.TeamIds, viewIds, userId, activeVirtualMachine.VmId);
                await Clients.Groups(groups).SendAsync(
                    VmHubMethods.ActiveVirtualMachine,
                    null,
                    userId,
                    null,
                    activeVirtualMachine.TeamIds);

                // Begin Handling of displaying current users connected to an individual VM
                var userNamesByGroup = await _activeVirtualMachineService.GetActiveVirtualMachineUsersByGroup(activeVirtualMachine.VmId, activeVirtualMachine, cancellationToken);

                foreach (var kvp in userNamesByGroup)
                {
                    await Clients.Groups(GetCurrentVmUsersChannelName(kvp.Key, activeVirtualMachine.VmId)).SendAsync(
                        VmHubMethods.CurrentVirtualMachineUsers,
                        activeVirtualMachine.VmId,
                        kvp.Value,
                        kvp.Key);
                }

                await _vmUsageLoggingService.CloseVmLogEntry(userId, activeVirtualMachine.VmId, cancellationToken);
                await _xApiService.TrackConsoleClosedAsync(
                    activeVirtualMachine.VmId,
                    activeVirtualMachine.TeamIds,
                    cancellationToken);
            }
        }

        private async Task UpdateVmUser(Guid userId, Guid vmId, IEnumerable<Guid> teamIds)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var teamId in teamIds)
            {
                var vmUser = new Domain.Models.VmUser(userId, vmId, teamId, now);
                _dbContext.Attach(vmUser);
                _dbContext.Update(vmUser);

                try
                {
                    await _dbContext.SaveChangesAsync();
                }
                catch (Exception)
                {
                    // If user doesn't exist, add them
                    // Only happens once per user per team in the system.
                    _dbContext.Add(vmUser);
                    await _dbContext.SaveChangesAsync();
                }
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
