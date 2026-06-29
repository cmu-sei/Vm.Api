// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Player.Api.Client;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Vm.Api.Domain.Services
{
    public interface IPlayerService
    {
        Task<IEnumerable<Team>> GetTeamsByViewIdAsync(Guid viewId, CancellationToken ct);

        /// <summary>
        /// All teams in the View, regardless of the caller's membership (unlike
        /// <see cref="GetTeamsByViewIdAsync"/>, which returns only the caller's teams). Backed by the
        /// privileged player.api GetViewTeams endpoint, so the caller must be authorized for it
        /// (e.g. a view-admin or system operator); use for the view-admin / all-views ISO listing.
        /// </summary>
        Task<IEnumerable<Team>> GetAllTeamsByViewIdAsync(Guid viewId, CancellationToken ct);
        Task<Team> GetPrimaryTeamByViewIdAsync(Guid viewId, CancellationToken ct);
        Task<Guid?> GetGroupIdForViewAsync(Guid viewId, CancellationToken ct);
        Task<View> GetViewByIdAsync(Guid viewId, CancellationToken ct);

        /// <summary>
        /// Every View in the system. Gated by SystemPermission.ViewViews in player.api, so the caller
        /// must be a system operator; use for the system-wide "all views" ISO listing.
        /// </summary>
        Task<IEnumerable<View>> GetAllViewsAsync(CancellationToken ct);
        Task<Team> GetTeamById(Guid id);
        Task<User> GetUserById(Guid id, CancellationToken ct);
        Task<IEnumerable<User>> GetUsersByTeamId(Guid teamId, CancellationToken ct);

        Task<bool> CanManageTeams(IEnumerable<Guid> teamIds, CancellationToken ct);
        Task<bool> CanViewTeams(IEnumerable<Guid> teamIds, CancellationToken ct);
        Task<bool> CanEditTeams(IEnumerable<Guid> teamIds, CancellationToken ct);
        Task<bool> HasViewNetworkAccess(IEnumerable<Guid> teamIds, CancellationToken ct);
        Task<bool> HasManageNetworkAccess(IEnumerable<Guid> teamIds, CancellationToken ct);
        Task<IEnumerable<Guid>> GetUserTeamIds(IEnumerable<Guid> teamIds, CancellationToken ct);

        Task<bool> Can(IEnumerable<Guid> teamIds,
                       IEnumerable<Guid> viewIds,
                       AppSystemPermission[] requiredSystemPermissions,
                       AppViewPermission[] requiredViewPermissions,
                       AppTeamPermission[] requiredTeamPermissions,
                       CancellationToken ct,
                       bool primaryTeamOnly = false);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly Guid _userId;
        private readonly IViewService _viewService;
        private readonly IMemoryCache _cache;
        private ConcurrentDictionary<Guid, ICollection<TeamPermissionsClaim>> _teamPermissionsCache = new();

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

        public async Task<bool> HasViewNetworkAccess(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            return await Can(teamIds, null, [AppSystemPermission.ViewNetworks, AppSystemPermission.ManageNetworks], [AppViewPermission.ViewNetworks, AppViewPermission.ManageNetworks], [], ct);
        }

        public async Task<bool> HasManageNetworkAccess(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            return await Can(teamIds, null, [AppSystemPermission.ManageNetworks], [AppViewPermission.ManageNetworks], [], ct);
        }

        public async Task<IEnumerable<Guid>> GetUserTeamIds(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            var userTeamIds = new List<Guid>();

            foreach (var teamId in teamIds)
            {
                var viewId = await _viewService.GetViewIdForTeam(teamId, ct);

                if (!viewId.HasValue)
                    continue;

                if (!_teamPermissionsCache.TryGetValue(viewId.Value, out var teamPermissionsClaims))
                {
                    teamPermissionsClaims = await _playerApiClient.GetMyTeamPermissionsAsync(viewId.Value, null, true);
                    _teamPermissionsCache.TryAdd(viewId.Value, teamPermissionsClaims);
                }

                if (teamPermissionsClaims != null && teamPermissionsClaims.Any(x => x.TeamId == teamId))
                {
                    userTeamIds.Add(teamId);
                }
            }

            return userTeamIds;
        }

        // When primaryTeamOnly is true the team- and view-level checks are scoped to the caller's
        // primary (active) team rather than "any team in the View" - used to make a listing follow
        // the active team. System permissions are unaffected by the flag: a passed required system
        // permission still short-circuits as normal (callers that don't want operator status to
        // count simply pass none).
        public async Task<bool> Can(IEnumerable<Guid> teamIds,
                       IEnumerable<Guid> viewIds,
                       AppSystemPermission[] requiredSystemPermissions,
                       AppViewPermission[] requiredViewPermissions,
                       AppTeamPermission[] requiredTeamPermissions,
                       CancellationToken ct,
                       bool primaryTeamOnly = false)
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
                if (!_teamPermissionsCache.TryGetValue(viewId, out var teamPermissionsClaims))
                {
                    teamPermissionsClaims = await _playerApiClient.GetMyTeamPermissionsAsync(viewId, null, true);
                    _teamPermissionsCache.TryAdd(viewId, teamPermissionsClaims);
                }

                if (teamPermissionsClaims != null)
                {
                    // Check View Permissions of all Teams in the View (or just the primary team).
                    var relevantClaims = primaryTeamOnly
                        ? teamPermissionsClaims.Where(x => x.IsPrimary)
                        : teamPermissionsClaims;

                    var appViewPermissions = relevantClaims
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
                    // When scoped to the active team, ignore any specified team that isn't primary.
                    if (primaryTeamOnly && !teamPermissionClaim.IsPrimary)
                        continue;

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

        public async Task<IEnumerable<Team>> GetAllTeamsByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            // All teams in the View (not just the caller's) - used for the view-admin ISO listing.
            var teams = await _playerApiClient.GetViewTeamsAsync(viewId, ct);
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

        public async Task<IEnumerable<View>> GetAllViewsAsync(CancellationToken ct)
        {
            // Every View in the system - gated by SystemPermission.ViewViews in player.api.
            return await _playerApiClient.GetViewsAsync(ct);
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
