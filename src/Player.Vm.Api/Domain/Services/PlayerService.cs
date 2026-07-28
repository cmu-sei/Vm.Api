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
        Task<VisibilityContext> GetVisibilityContextAsync(Guid viewId, CancellationToken ct);
        Task<VisibilityContext> GetVisibilityContextForTeamAsync(Guid teamId, CancellationToken ct);
        Task<bool> IsTeamInViewAsync(Guid teamId, Guid viewId, CancellationToken ct);
        Task<bool> IsTeamVisibleAsync(Guid teamId, CancellationToken ct);
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

        Task<IEnumerable<Guid>> GetGroupIdsForViewAsync(Guid viewId, CancellationToken ct);
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
                       CancellationToken ct);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly Guid _userId;
        private readonly IViewService _viewService;
        private readonly IMemoryCache _cache;
        private ConcurrentDictionary<Guid, ICollection<TeamPermissionsClaim>> _teamPermissionsCache = new();
        private ConcurrentDictionary<Guid, ICollection<Team>> _viewTeamsCache = new();

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
            return await Can(
                teamIds,
                null,
                [AppSystemPermission.ManageViews],
                [AppViewPermission.ManageView],
                [AppTeamPermission.ManageTeam],
                ct);
        }

        public async Task<bool> CanViewTeams(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            return await Can(
                teamIds,
                null,
                [AppSystemPermission.ViewViews, AppSystemPermission.ManageViews],
                [AppViewPermission.ViewView, AppViewPermission.ManageView],
                [AppTeamPermission.ViewTeam, AppTeamPermission.ManageTeam],
                ct);
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

            foreach (var teamId in teamIds ?? [])
            {
                var viewId = await _viewService.GetViewIdForTeam(teamId, ct);

                if (!viewId.HasValue)
                    continue;

                var teamPermissionsClaims = await GetTeamPermissionsByViewIdAsync(viewId.Value, ct);

                if (teamPermissionsClaims != null && teamPermissionsClaims.Any(x => x.TeamId == teamId))
                {
                    userTeamIds.Add(teamId);
                }
            }

            return userTeamIds;
        }

        public async Task<bool> Can(IEnumerable<Guid> teamIds,
                       IEnumerable<Guid> viewIds,
                       AppSystemPermission[] requiredSystemPermissions,
                       AppViewPermission[] requiredViewPermissions,
                       AppTeamPermission[] requiredTeamPermissions,
                       CancellationToken ct)
        {
            var requestedTeamIds = (teamIds ?? []).Where(x => x != Guid.Empty).Distinct().ToArray();
            var requestedViewIds = (viewIds ?? []).Where(x => x != Guid.Empty).Distinct().ToArray();
            requiredSystemPermissions ??= [];
            requiredViewPermissions ??= [];
            requiredTeamPermissions ??= [];

            ICollection<string> systemPermissions;

            if (!_cache.TryGetValue(_userId, out systemPermissions))
            {
                systemPermissions = await _playerApiClient.GetMyPermissionsAsync(ct);
                _cache.Set(_userId, systemPermissions, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(1)));
            }

            var appSystemPermissions = (systemPermissions ?? [])
                .Select(x => Enum.TryParse<AppSystemPermission>(x, out var p) ? p : (AppSystemPermission?)null)
                .Where(p => p.HasValue)
                .Select(p => p.Value);

            if (requiredSystemPermissions.Any() && appSystemPermissions.Intersect(requiredSystemPermissions).Any())
                return true;

            var teamViewIds = new Dictionary<Guid, Guid>();

            foreach (var teamId in requestedTeamIds)
            {
                var viewId = await _viewService.GetViewIdForTeam(teamId, ct);

                if (viewId.HasValue)
                    teamViewIds[teamId] = viewId.Value;
            }

            var allViewIds = requestedViewIds
                .Concat(teamViewIds.Values)
                .Distinct()
                .ToArray();

            foreach (var viewId in allViewIds)
            {
                var teamPermissionsClaims = await GetTeamPermissionsByViewIdAsync(viewId, ct);

                if (requiredViewPermissions.Any() && teamPermissionsClaims != null)
                {
                    // This intentionally mirrors player.api's TeamPermissionRequirement /
                    // GetPrimaryVisibilityContext. Check the requested teams first, then fall back to
                    // the caller's own (member or primary) teams - NOT every claim in the View, which
                    // would let a scoped-in claim authorize an operation on an unrelated team.
                    var targetTeamIds = teamViewIds
                        .Where(x => x.Value == viewId)
                        .Select(x => x.Key)
                        .ToArray();

                    if (targetTeamIds.Any() && HasViewPermission(teamPermissionsClaims, targetTeamIds, requiredViewPermissions))
                        return true;

                    var directTeamIds = await GetDirectTeamIdsByViewIdAsync(viewId, ct);

                    if (directTeamIds.Any() && HasViewPermission(teamPermissionsClaims, directTeamIds, requiredViewPermissions))
                        return true;
                }

                if (requiredTeamPermissions.Any() && teamPermissionsClaims != null)
                {
                    var targetTeamIds = teamViewIds
                        .Where(x => x.Value == viewId)
                        .Select(x => x.Key)
                        .ToArray();

                    if (targetTeamIds.Any() && HasTeamPermission(teamPermissionsClaims, targetTeamIds, requiredTeamPermissions))
                        return true;
                }
            }

            return false;
        }

        public async Task<IEnumerable<Team>> GetTeamsByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            try
            {
                // Fetch teams FIRST - this is the probe that surfaces a 404 for an unknown View.
                // GetVisibilityContextAsync deliberately swallows 404 into an empty context, so
                // reversing these two lines would turn "view not found" into "no visible teams".
                var teams = await GetUserViewTeamsByViewIdAsync(viewId, ct);
                var visibility = await GetVisibilityContextAsync(viewId, ct);
                return teams.Where(x => visibility.TeamIds.Contains(x.Id));
            }
            catch (Player.Api.Client.ApiException ex) when (ex.StatusCode == 404)
            {
                // View not found in Player API - return null to allow caller to handle
                return null;
            }
        }

        public async Task<IEnumerable<Team>> GetAllTeamsByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            // All teams in the View (not just the caller's) - used for the view-admin ISO listing.
            var teams = await _playerApiClient.GetViewTeamsAsync(viewId, ct);
            return teams;
        }

        public async Task<Team> GetPrimaryTeamByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            try
            {
                // Teams first - see the ordering note in GetTeamsByViewIdAsync.
                var teams = await GetUserViewTeamsByViewIdAsync(viewId, ct);
                var visibility = await GetVisibilityContextAsync(viewId, ct);
                return teams.FirstOrDefault(x => x.Id == visibility.PrimaryTeamId);
            }
            catch (Player.Api.Client.ApiException ex) when (ex.StatusCode == 404)
            {
                // View not found in Player API - return null to allow caller to handle
                return null;
            }
        }

        public async Task<IEnumerable<Guid>> GetGroupIdsForViewAsync(Guid viewId, CancellationToken ct)
        {
            var visibility = await GetVisibilityContextAsync(viewId, ct);
            if (visibility.CanViewAllTeams)
                return [viewId];

            return visibility.TeamIds;
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
            var visibility = await GetVisibilityContextAsync(viewId, ct);
            if (!visibility.PrimaryTeamId.HasValue)
                return null;

            if (visibility.CanViewAllTeams)
                return viewId;

            return visibility.PrimaryTeamId;
        }

        public async Task<VisibilityContext> GetVisibilityContextAsync(Guid viewId, CancellationToken ct)
        {
            ICollection<TeamPermissionsClaim> permissions;

            try
            {
                permissions = await GetTeamPermissionsByViewIdAsync(viewId, ct);
            }
            catch (Player.Api.Client.ApiException ex) when (ex.StatusCode == 404)
            {
                // View not found in Player API - an unknown View grants no visibility. Callers that
                // need to distinguish "not found" from "nothing visible" (GetTeamsByViewIdAsync,
                // GetPrimaryTeamByViewIdAsync) probe the teams endpoint separately.
                return VisibilityContext.Empty;
            }

            var primaryPermission = permissions.FirstOrDefault(x => x.IsPrimary);

            if (primaryPermission == null)
                return VisibilityContext.Empty;

            // This intentionally mirrors player.api's
            // AuthorizationService.GetPrimaryVisibilityContext, which decides visibility from the
            // primary team's DIRECT permissions only - permissions scoped in from another team must
            // not widen what that team can see.
            var directViewPermissions = ParsePermissions<AppViewPermission>(primaryPermission.DirectPermissionValues);
            var directTeamPermissions = ParsePermissions<AppTeamPermission>(primaryPermission.DirectPermissionValues);
            var canViewAllTeams =
                directViewPermissions.Contains(AppViewPermission.ViewView) ||
                directViewPermissions.Contains(AppViewPermission.ManageView);
            var visibleTeamIds = new HashSet<Guid> { primaryPermission.TeamId };

            if (canViewAllTeams)
            {
                var teams = await GetUserViewTeamsByViewIdAsync(viewId, ct);
                visibleTeamIds.UnionWith(teams.Select(x => x.Id));
            }
            else if (
                directTeamPermissions.Contains(AppTeamPermission.ViewTeam) ||
                directTeamPermissions.Contains(AppTeamPermission.ManageTeam))
            {
                // Teams that scoped their permissions onto the primary team. The caller's own direct
                // ViewTeam/ManageTeam above is what authorizes this - the scoped claim's own
                // permissions are deliberately not consulted, matching player.api.
                visibleTeamIds.UnionWith(permissions
                    .Where(x => x.SourceTeamIds?.Contains(primaryPermission.TeamId) ?? false)
                    .Select(x => x.TeamId));
            }

            visibleTeamIds.Remove(Guid.Empty);
            return new VisibilityContext(primaryPermission.TeamId, canViewAllTeams, visibleTeamIds);
        }

        public async Task<bool> IsTeamInViewAsync(Guid teamId, Guid viewId, CancellationToken ct)
        {
            var teamViewId = await _viewService.GetViewIdForTeam(teamId, ct);
            return teamViewId == viewId;
        }

        public async Task<bool> IsTeamVisibleAsync(Guid teamId, CancellationToken ct)
        {
            var visibility = await GetVisibilityContextForTeamAsync(teamId, ct);
            return visibility.TeamIds.Contains(teamId);
        }

        public async Task<VisibilityContext> GetVisibilityContextForTeamAsync(Guid teamId, CancellationToken ct)
        {
            var viewId = await _viewService.GetViewIdForTeam(teamId, ct);
            if (!viewId.HasValue)
                return VisibilityContext.Empty;

            return await GetVisibilityContextAsync(viewId.Value, ct);
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

        private async Task<ICollection<TeamPermissionsClaim>> GetTeamPermissionsByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            if (!_teamPermissionsCache.TryGetValue(viewId, out var teamPermissionsClaims))
            {
                teamPermissionsClaims = await _playerApiClient.GetMyTeamPermissionsAsync(viewId, null, true, ct);

                // Only cache a real result - memoizing null would make a transient failure sticky for
                // the rest of the request and indistinguishable from "no claims".
                if (teamPermissionsClaims != null)
                    _teamPermissionsCache.TryAdd(viewId, teamPermissionsClaims);
            }

            return teamPermissionsClaims ?? [];
        }

        private async Task<ICollection<Team>> GetUserViewTeamsByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            if (!_viewTeamsCache.TryGetValue(viewId, out var teams))
            {
                teams = await _playerApiClient.GetUserViewTeamsAsync(viewId, _userId, ct);

                // Only cache a real result - memoizing null would make a transient failure sticky for
                // the rest of the request and indistinguishable from "view exists, no teams".
                if (teams != null)
                    _viewTeamsCache.TryAdd(viewId, teams);
            }

            return teams ?? [];
        }

        // The caller's own teams in the View - membership or primary only, excluding teams reachable
        // solely through a scoped permission grant.
        private async Task<HashSet<Guid>> GetDirectTeamIdsByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            var teams = await GetUserViewTeamsByViewIdAsync(viewId, ct);
            return teams
                .Where(x => x.IsMember || x.IsPrimary)
                .Select(x => x.Id)
                .ToHashSet();
        }

        private static bool HasViewPermission(ICollection<TeamPermissionsClaim> claims, IEnumerable<Guid> teamIds, AppViewPermission[] requiredPermissions)
        {
            return claims
                .Where(x => teamIds.Contains(x.TeamId))
                .SelectMany(x => x.PermissionValues ?? [])
                .Select(x => Enum.TryParse<AppViewPermission>(x, out var p) ? p : (AppViewPermission?)null)
                .Where(p => p.HasValue)
                .Select(p => p.Value)
                .Intersect(requiredPermissions)
                .Any();
        }

        private static HashSet<TPermission> ParsePermissions<TPermission>(IEnumerable<string> permissionValues)
            where TPermission : struct, Enum
        {
            return (permissionValues ?? [])
                .Select(x => Enum.TryParse<TPermission>(x, out var permission) ? permission : (TPermission?)null)
                .Where(p => p.HasValue)
                .Select(p => p.Value)
                .ToHashSet();
        }

        private static bool HasTeamPermission(ICollection<TeamPermissionsClaim> claims, IEnumerable<Guid> teamIds, AppTeamPermission[] requiredPermissions)
        {
            return claims
                .Where(x => teamIds.Contains(x.TeamId))
                .SelectMany(x => x.PermissionValues ?? [])
                .Select(x => Enum.TryParse<AppTeamPermission>(x, out var p) ? p : (AppTeamPermission?)null)
                .Where(p => p.HasValue)
                .Select(p => p.Value)
                .Intersect(requiredPermissions)
                .Any();
        }
    }

    public sealed class VisibilityContext
    {
        public static VisibilityContext Empty { get; } = new(null, false, []);

        public VisibilityContext(Guid? primaryTeamId, bool canViewAllTeams, HashSet<Guid> teamIds)
        {
            PrimaryTeamId = primaryTeamId;
            CanViewAllTeams = canViewAllTeams;
            TeamIds = teamIds;
        }

        public Guid? PrimaryTeamId { get; }
        public bool CanViewAllTeams { get; }
        public IReadOnlySet<Guid> TeamIds { get; }
    }
}
