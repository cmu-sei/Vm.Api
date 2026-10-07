// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// What player.api knows, as far as vm.api asks: which View each team is in, and per caller the system
/// permissions and the team permission claims player.api would return. It answers a substituted
/// <see cref="IPlayerApiClient"/> (the generated client, the thing that leaves the process) and the team
/// lookups of a substituted <see cref="IViewService"/>, so the real <see cref="PlayerService"/> above them
/// makes every authorization and visibility decision.
/// </summary>
/// <remarks>
/// <para>
/// The <c>GetMy*</c> routes answer for the caller, which player.api reads from the bearer token. Here the
/// caller is whoever <c>currentUser</c> names: the user of the request in flight, in a hosted app
/// (<see cref="Microsoft.AspNetCore.Http.HttpContextAccessor"/> reads the ambient request), or the service
/// test's own caller.
/// </para>
/// <para>
/// Every team is placed in one View (<see cref="PlaceTeam"/>). <c>GetUserViewTeamsAsync</c> returns the
/// teams the user's primary visibility context reaches, with its membership flags; the privileged
/// <c>GetViewTeamsAsync</c> and <c>GetViewsAsync</c> answer 403 without the permissions player.api asks
/// for them; a View nobody placed a team in, or added, answers 404 as player.api does. A caller's claim on a team carries its
/// permissions as direct ones (granted by that team's own role), unless it is scoped in from another team.
/// Tests arrange callers through <see cref="TestActorBuilder"/>, not here.
/// </para>
/// </remarks>
public sealed class PlayerDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Guid> _teamViews = [];
    private readonly Dictionary<Guid, string> _teamNames = [];
    private readonly HashSet<Guid> _views = [];
    private readonly Dictionary<Guid, string> _viewNames = [];
    private readonly Dictionary<Guid, PlayerCaller> _callers = [];
    private readonly IPlayerApiClient _client;
    private readonly IViewService _viewService;
    private readonly Func<Guid?> _currentUser;

    public PlayerDirectory(IPlayerApiClient client, IViewService views, Func<Guid?> currentUser)
    {
        _client = client;
        _viewService = views;
        _currentUser = currentUser;

        Answer();
    }

    /// <summary>Puts <paramref name="teamId"/> in <paramref name="viewId"/>, adding the View if it is new.</summary>
    public void PlaceTeam(Guid teamId, Guid viewId, string name = null)
    {
        lock (_gate)
        {
            _views.Add(viewId);
            _teamViews[teamId] = viewId;
            _teamNames[teamId] = name ?? $"team-{teamId}";
        }
    }

    /// <summary>Places the team unless it is already in that View, keeping its name.</summary>
    private void PlaceLocked(Guid teamId, Guid viewId)
    {
        _views.Add(viewId);

        if (_teamViews.GetValueOrDefault(teamId) != viewId)
        {
            _teamViews[teamId] = viewId;
            _teamNames.TryAdd(teamId, $"team-{teamId}");
        }
    }

    /// <summary>Makes <paramref name="viewId"/> a View player.api has, with no teams yet.</summary>
    public void AddView(Guid viewId, string name = null)
    {
        lock (_gate)
        {
            _views.Add(viewId);

            if (name is not null)
                _viewNames[viewId] = name;
        }
    }

    /// <summary>The View a team was placed in, or null.</summary>
    public Guid? ViewOf(Guid teamId)
    {
        lock (_gate)
        {
            return _teamViews.TryGetValue(teamId, out var viewId) ? viewId : null;
        }
    }

    /// <summary>The grants player.api holds for <paramref name="userId"/>, created empty on first use.</summary>
    public PlayerCaller Caller(Guid userId)
    {
        lock (_gate)
        {
            return CallerLocked(userId);
        }
    }

    private PlayerCaller CallerLocked(Guid userId)
    {
        if (!_callers.TryGetValue(userId, out var caller))
        {
            caller = new PlayerCaller();
            _callers[userId] = caller;
        }

        return caller;
    }

    /// <summary>
    /// Writes the player.api data a visibility context comes from, for <paramref name="userId"/> in
    /// <paramref name="viewId"/>, replacing its claims there: the primary team, member and primary; with
    /// <paramref name="canViewAllTeams"/>, <c>ViewView</c> on it (the first of <paramref name="teamIds"/> is
    /// primary when none is named), so every team of the View is visible; otherwise <c>ViewTeam</c> on it
    /// and each other team scoped onto it. Neither is a caller who sees nothing there. The teams are placed
    /// in the View.
    /// </summary>
    public void Sees(Guid userId, Guid viewId, Guid? primaryTeamId, bool canViewAllTeams, IEnumerable<Guid> teamIds)
    {
        var teams = teamIds.ToArray();

        lock (_gate)
        {
            _views.Add(viewId);

            foreach (var team in teams)
                PlaceLocked(team, viewId);

            var caller = CallerLocked(userId);

            foreach (var stale in caller.Claims.Keys.Where(x => _teamViews.GetValueOrDefault(x) == viewId).ToArray())
                caller.Claims.Remove(stale);

            var primary = primaryTeamId ?? (canViewAllTeams ? teams.FirstOrDefault() : Guid.Empty);

            if (primary == Guid.Empty)
            {
                if (!canViewAllTeams)
                    return;

                primary = Guid.NewGuid();
            }

            PlaceLocked(primary, viewId);
            var claim = caller.ClaimOn(primary);
            claim.Primary = true;
            claim.Member = true;

            if (canViewAllTeams)
            {
                claim.Grant(nameof(AppViewPermission.ViewView));
                return;
            }

            var others = teams.Where(x => x != primary).ToArray();

            if (others.Length > 0)
                claim.Grant(nameof(AppTeamPermission.ViewTeam));

            // A scoped claim carries the effective permissions of the membership that grants it.
            foreach (var other in others)
            {
                var scoped = caller.ClaimOn(other);
                scoped.Sources.Add(primary);
                scoped.Permissions.UnionWith(claim.Permissions);
            }
        }
    }

    /// <summary>Forgets every team, View and caller, and answers the substitutes afresh.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _teamViews.Clear();
            _teamNames.Clear();
            _views.Clear();
            _viewNames.Clear();
            _callers.Clear();
        }

        Answer();
    }

    private void Answer()
    {
        _client.GetMyPermissionsAsync(default)
            .ReturnsForAnyArgs(_ => Task.FromResult<ICollection<string>>(SystemOf(_currentUser())));

        _client.GetMyTeamPermissionsAsync(default, default, default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(ClaimsOf(_currentUser(), call.ArgAt<Guid?>(0))));

        _client.GetUserViewTeamsAsync(default, default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(Roster(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1))));

        _client.GetViewTeamsAsync(default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(AllTeams(_currentUser(), call.ArgAt<Guid>(0))));

        _client.GetTeamAsync(default)
            .ReturnsForAnyArgs(call => Task.FromResult(Team(call.ArgAt<Guid>(0))));
        _client.GetTeamAsync(default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(Team(call.ArgAt<Guid>(0))));

        _client.GetViewAsync(default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(View(call.ArgAt<Guid>(0))));

        _client.GetViewsAsync(default)
            .ReturnsForAnyArgs(_ => Task.FromResult<ICollection<View>>(Views(_currentUser())));

        _viewService.GetViewIdForTeam(default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(ViewOf(call.ArgAt<Guid>(0))));
    }

    private List<string> SystemOf(Guid? userId)
    {
        if (userId is null || userId == Guid.Empty)
            throw Unauthorized();

        lock (_gate)
        {
            return userId is { } id && _callers.TryGetValue(id, out var caller)
                ? [.. caller.System.Select(x => x.ToString())]
                : [];
        }
    }

    private ICollection<TeamPermissionsClaim> ClaimsOf(Guid? userId, Guid? viewId)
    {
        if (userId is null || userId == Guid.Empty)
            throw Unauthorized();

        lock (_gate)
        {
            if (userId is not { } id || !_callers.TryGetValue(id, out var caller))
            {
                return [];
            }

            return [.. caller.Claims
                .Where(x => viewId is null || (_teamViews.TryGetValue(x.Key, out var v) && v == viewId))
                .Select(x => new TeamPermissionsClaim
                {
                    TeamId = x.Key,
                    PermissionValues = [.. x.Value.Permissions],
                    DirectPermissionValues = [.. x.Value.Direct],
                    IsPrimary = x.Value.Primary,
                    SourceTeamIds = [.. x.Value.Sources],
                })];
        }
    }

    /// <summary>
    /// <c>GetUserViewTeamsAsync</c>: the teams of the View the user's primary visibility context reaches,
    /// as player.api computes it from the primary team's direct permissions. No primary team there is no
    /// team at all; <c>ViewView</c> or <c>ManageView</c> on it is every team of the View; <c>ViewTeam</c> or
    /// <c>ManageTeam</c> is the primary team and the teams scoped from it; anything else the primary alone.
    /// </summary>
    private ICollection<Team> Roster(Guid viewId, Guid userId)
    {
        lock (_gate)
        {
            if (!_views.Contains(viewId))
            {
                throw NotFound($"View {viewId}");
            }

            _callers.TryGetValue(userId, out var caller);
            var claims = caller?.Claims.Where(x => _teamViews.GetValueOrDefault(x.Key) == viewId).ToArray() ?? [];
            var primary = claims.FirstOrDefault(x => x.Value.Primary);

            if (primary.Value is null)
            {
                return [];
            }

            var direct = primary.Value.Direct;
            IEnumerable<Guid> visible = direct.Contains(nameof(AppViewPermission.ViewView)) || direct.Contains(nameof(AppViewPermission.ManageView))
                ? _teamViews.Where(x => x.Value == viewId).Select(x => x.Key)
                : direct.Contains(nameof(AppTeamPermission.ViewTeam)) || direct.Contains(nameof(AppTeamPermission.ManageTeam))
                    ? claims.Where(x => x.Value.Sources.Contains(primary.Key)).Select(x => x.Key).Prepend(primary.Key)
                    : [primary.Key];

            return [.. visible.Distinct().Select(teamId =>
            {
                var claim = caller.Claims.GetValueOrDefault(teamId);

                return new Team
                {
                    Id = teamId,
                    Name = _teamNames[teamId],
                    ViewId = viewId,
                    IsMember = claim?.Member ?? false,
                    IsPrimary = claim?.Primary ?? false,
                };
            })];
        }
    }

    /// <summary>
    /// <c>GetViewTeamsAsync</c>, the privileged listing: every team of the View, for a caller holding the
    /// system <c>ViewViews</c> or <c>ViewView</c> on the View; 403 for anyone else.
    /// </summary>
    private ICollection<Team> AllTeams(Guid? userId, Guid viewId)
    {
        lock (_gate)
        {
            if (!_views.Contains(viewId))
            {
                throw NotFound($"View {viewId}");
            }

            var caller = CallerOrThrow(userId);
            var viewView = caller.Claims
                .Where(x => _teamViews.GetValueOrDefault(x.Key) == viewId)
                .Any(x => x.Value.Permissions.Contains(nameof(AppViewPermission.ViewView)));

            if (!caller.System.Contains(AppSystemPermission.ViewViews) && !viewView)
            {
                throw Forbidden();
            }

            return [.. _teamViews
                .Where(x => x.Value == viewId)
                .Select(x => new Team { Id = x.Key, Name = _teamNames[x.Key], ViewId = viewId })];
        }
    }

    private PlayerCaller CallerOrThrow(Guid? userId) =>
        userId is null || userId == Guid.Empty
            ? throw Unauthorized()
            : _callers.GetValueOrDefault(userId.Value) ?? new PlayerCaller();

    private Team Team(Guid teamId)
    {
        lock (_gate)
        {
            return _teamViews.TryGetValue(teamId, out var viewId)
                ? new Team { Id = teamId, Name = _teamNames[teamId], ViewId = viewId }
                : throw NotFound($"Team {teamId}");
        }
    }

    private View View(Guid viewId)
    {
        lock (_gate)
        {
            return _views.Contains(viewId)
                ? new View { Id = viewId, Name = ViewName(viewId) }
                : throw NotFound($"View {viewId}");
        }
    }

    /// <summary><c>GetViewsAsync</c>: every View, for a caller holding the system <c>ViewViews</c>; 403 otherwise.</summary>
    private List<View> Views(Guid? userId)
    {
        lock (_gate)
        {
            if (!CallerOrThrow(userId).System.Contains(AppSystemPermission.ViewViews))
            {
                throw Forbidden();
            }

            return [.. _views.Select(x => new View { Id = x, Name = ViewName(x) })];
        }
    }

    private string ViewName(Guid viewId) => _viewNames.GetValueOrDefault(viewId) ?? $"view-{viewId.ToString()[..8]}";

    /// <summary>
    /// What player.api answers a <c>GetMy*</c> route that carries no caller: vm.api forwards the request's
    /// own <c>Authorization</c> header, which an anonymous request does not have, and player.api refuses it.
    /// </summary>
    private static ApiException Unauthorized() =>
        new("Unauthorized", 401, null, new Dictionary<string, IEnumerable<string>>(), null);

    private static ApiException Forbidden() =>
        new("Forbidden", 403, null, new Dictionary<string, IEnumerable<string>>(), null);

    private static ApiException NotFound(string what) =>
        new($"{what} not found", 404, null, new Dictionary<string, IEnumerable<string>>(), null);
}

/// <summary>What player.api holds for one caller: system permissions, and a claim per team.</summary>
public sealed class PlayerCaller
{
    public HashSet<AppSystemPermission> System { get; } = [];

    public Dictionary<Guid, PlayerClaim> Claims { get; } = [];

    /// <summary>The caller's claim on <paramref name="teamId"/>, created empty on first use.</summary>
    public PlayerClaim ClaimOn(Guid teamId)
    {
        if (!Claims.TryGetValue(teamId, out var claim))
        {
            claim = new PlayerClaim();
            Claims[teamId] = claim;
        }

        return claim;
    }
}

/// <summary>
/// One team permission claim: the permission values player.api reports for the team, the ones granted by
/// the team's own role (direct), whether it is the caller's primary team or one it is a member of, and the
/// teams it is scoped in from.
/// </summary>
public sealed class PlayerClaim
{
    public HashSet<string> Permissions { get; } = [];

    public HashSet<string> Direct { get; } = [];

    public HashSet<Guid> Sources { get; } = [];

    public bool Member { get; set; }

    public bool Primary { get; set; }

    /// <summary>Grants <paramref name="permission"/> through the team's own role.</summary>
    public PlayerClaim Grant(string permission)
    {
        Permissions.Add(permission);
        Direct.Add(permission);
        return this;
    }
}
