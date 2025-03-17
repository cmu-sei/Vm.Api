// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Player.Api.Client;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Vm.Api.Domain.Services
{
    public interface IPlayerService
    {
        Task<IEnumerable<Team>> GetTeamsByViewIdAsync(Guid viewId, CancellationToken ct);
        Task<Team> GetPrimaryTeamByViewIdAsync(Guid viewId, CancellationToken ct);
        Task<Guid?> GetGroupIdForViewAsync(Guid viewId, CancellationToken ct);
        Task<View> GetViewByIdAsync(Guid viewId, CancellationToken ct);
        Task<Team> GetTeamById(Guid id);
        Task<User> GetUserById(Guid id, CancellationToken ct);
        Task<IEnumerable<User>> GetUsersByTeamId(Guid teamId, CancellationToken ct);

        Task<bool> CanManageTeams(IEnumerable<Guid> teamIds, CancellationToken ct);
        Task<bool> CanViewTeams(IEnumerable<Guid> teamIds, CancellationToken ct);
        Task<bool> CanEditTeams(IEnumerable<Guid> teamIds, CancellationToken ct);

        Task<bool> Can(IEnumerable<Guid> teamIds,
                       IEnumerable<Guid> viewIds,
                       AppSystemPermission[] requiredSystemPermissions,
                       AppViewPermission[] requiredViewPermissions,
                       AppTeamPermission[] requiredTeamPermissions,
                       CancellationToken ct);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly Guid _userId;
        private readonly IViewService _viewService;
        private readonly IMemoryCache _cache;
        private Dictionary<Guid, ICollection<TeamPermissionsClaim>> _teamPermissionsCache = new();

        public PlayerService(IHttpContextAccessor httpContextAccessor, IPlayerApiClient playerApiClient, IViewService viewService, IMemoryCache cache)
        {
            try
            {
                _userId = httpContextAccessor.HttpContext.User.GetId();
            }
            catch (Exception)
            {
                _userId = Guid.Empty;
            }
            _playerApiClient = playerApiClient;
            _viewService = viewService;
            _cache = cache;
        }

        public async Task<bool> CanManageTeams(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            return await Can(teamIds, null, [AppSystemPermission.ManageViews], [AppViewPermission.ManageView], [], ct);
        }

        public async Task<bool> CanViewTeams(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            return await Can(teamIds, null, [AppSystemPermission.ViewViews], [AppViewPermission.ViewView], [AppTeamPermission.ViewTeam], ct);
        }

        public async Task<bool> CanEditTeams(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            return await Can(teamIds, null, [AppSystemPermission.EditViews], [AppViewPermission.EditView], [AppTeamPermission.EditTeam], ct);
        }

        public async Task<bool> Can(IEnumerable<Guid> teamIds,
                       IEnumerable<Guid> viewIds,
                       AppSystemPermission[] requiredSystemPermissions,
                       AppViewPermission[] requiredViewPermissions,
                       AppTeamPermission[] requiredTeamPermissions,
                       CancellationToken ct)
        {
            ICollection<string> systemPermissions;

            if (!_cache.TryGetValue(_userId, out systemPermissions))
            {
                systemPermissions = await _playerApiClient.GetMyPermissionsAsync(ct);
                _cache.Set(_userId, systemPermissions, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(1)));
            }

            var appSystemPermissions = systemPermissions
                .Select(x => Enum.TryParse<AppSystemPermission>(x, out var p) ? p : (AppSystemPermission?)null)
                .Where(p => p.HasValue)
                .Select(p => p.Value);

            if (appSystemPermissions.Intersect(requiredSystemPermissions).Any())
                return true;

            List<Guid> allViewIds = new(viewIds ?? []);

            if (teamIds.Any())
            {
                allViewIds.AddRange(await _viewService.GetViewIdsForTeams(teamIds, ct));
            }

            foreach (var viewId in allViewIds)
            {
                ICollection<TeamPermissionsClaim> teamPermissionsClaims;

                if (!_teamPermissionsCache.TryGetValue(viewId, out teamPermissionsClaims))
                {
                    teamPermissionsClaims = await _playerApiClient.GetMyTeamPermissionsAsync(viewId, null, true);
                    _teamPermissionsCache.Add(viewId, teamPermissionsClaims);
                }

                if (teamPermissionsClaims != null)
                {
                    // Check View Permissions of all Teams in the View
                    var appViewPermissions = teamPermissionsClaims
                        .SelectMany(x => x.PermissionValues)
                        .Select(x => Enum.TryParse<AppViewPermission>(x, out var p) ? p : (AppViewPermission?)null)
                        .Where(p => p.HasValue)
                        .Select(p => p.Value);

                    if (appViewPermissions.Intersect(requiredViewPermissions).Any())
                    {
                        return true;
                    }
                }
            }

            foreach (var teamId in teamIds)
            {
                var viewId = await _viewService.GetViewIdForTeam(teamId, ct);
                var viewPermissions = _teamPermissionsCache.Where(x => x.Key == viewId).FirstOrDefault().Value;
                var teamPermissionClaim = viewPermissions.Where(x => x.TeamId == teamId).FirstOrDefault();

                if (teamPermissionClaim != null)
                {
                    // Check Team Permissions of just the specified Team
                    var appTeamPermissions = teamPermissionClaim?.PermissionValues
                        .Select(x => Enum.TryParse<AppTeamPermission>(x, out var p) ? p : (AppTeamPermission?)null)
                        .Where(p => p.HasValue)
                        .Select(p => p.Value) ?? [];

                    if (appTeamPermissions.Intersect(requiredTeamPermissions).Any())
                        return true;
                }
            }

            return false;
        }

        public async Task<IEnumerable<Team>> GetTeamsByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            var teams = await _playerApiClient.GetUserViewTeamsAsync(viewId, _userId, ct);
            return teams;
        }

        public async Task<Team> GetPrimaryTeamByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            var teams = await _playerApiClient.GetUserViewTeamsAsync(viewId, _userId, ct);

            return teams
                .Where(t => t.IsPrimary)
                .FirstOrDefault();
        }

        public async Task<Team> GetTeamById(Guid id)
        {
            try
            {
                return await _playerApiClient.GetTeamAsync(id);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<Guid?> GetGroupIdForViewAsync(Guid viewId, CancellationToken ct)
        {
            Guid? groupId = null;
            var permissions = await _playerApiClient.GetMyTeamPermissionsAsync(viewId, null, null, ct);

            if (permissions != null)
            {
                var appViewPermissions = permissions
                    .SelectMany(x => x.PermissionValues)
                    .Select(x => Enum.TryParse<AppViewPermission>(x, out var p) ? p : (AppViewPermission?)null)
                    .Where(p => p.HasValue)
                    .Select(p => p.Value);

                if (appViewPermissions.Contains(AppViewPermission.ViewView))
                {
                    groupId = viewId;
                }
                else
                {
                    var primaryTeamPermissions = permissions.Where(x => x.IsPrimary).FirstOrDefault();

                    if (primaryTeamPermissions != null)
                    {
                        groupId = primaryTeamPermissions.TeamId;
                    }
                }
            }

            return groupId;
        }

        public async Task<View> GetViewByIdAsync(Guid viewId, CancellationToken ct)
        {
            return await _playerApiClient.GetViewAsync(viewId, ct);
        }

        public async Task<User> GetUserById(Guid id, CancellationToken ct)
        {
            return await _playerApiClient.GetUserAsync(id, ct);
        }

        public async Task<IEnumerable<User>> GetUsersByTeamId(Guid teamId, CancellationToken ct)
        {
            return await _playerApiClient.GetTeamUsersAsync(teamId, ct);
        }
    }
}
