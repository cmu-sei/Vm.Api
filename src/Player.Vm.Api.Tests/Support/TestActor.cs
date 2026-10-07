// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api keeps no users, roles or permission rows: every permission decision is PlayerService's, made from
// what player.api answers. So the actor is the id and name a request carries (the sub and name claims the
// shared TestAuthHandler mints), there are no rows to seed, and each step below is the player.api data it
// implies, written to the factory's PlayerDirectory: a system permission, a claim on a team (a team
// permission, or a View permission through one of the actor's own teams in that View), a team's View,
// the actor's primary team (its first membership in a View unless PrimaryOn names another, as player.api
// assigns it). The real PlayerService turns that into decisions and visibility.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Tests.Support;

/// <summary>A caller: the identity a request carries.</summary>
public sealed class TestActor
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Sent as the <c>name</c> claim, which vm.api reads into the usage log and the hub presence
    /// (<c>ClaimsPrincipal.GetName</c>).
    /// </summary>
    public required string Name { get; init; }
}

/// <summary>
/// Describes a caller as player.api knows it. <see cref="SeedAsync"/> writes the steps to the
/// <see cref="PlayerDirectory"/> under the actor's id; nothing else answers for that id.
/// </summary>
public sealed class TestActorBuilder(PlayerDirectory player, CancellationToken ct)
{
    private readonly List<(Guid Team, Guid View)> _placements = [];
    private readonly List<AppSystemPermission> _system = [];
    private readonly List<(Guid? Team, AppTeamPermission Permission)> _team = [];
    private readonly List<(Guid? View, AppViewPermission Permission)> _view = [];
    private readonly List<Guid> _primary = [];
    private readonly List<Guid> _member = [];
    private readonly List<Guid> _scoped = [];
    private readonly List<Guid> _memberships = [];
    private Guid _id = Guid.NewGuid();
    private string _name = "Test Actor";

    /// <summary>Fixes the actor's id, for a test that needs to know it before the request.</summary>
    public TestActorBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public TestActorBuilder WithName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _name = name;
        return this;
    }

    /// <summary>System permissions, which <c>PlayerService.Can</c> counts for every team and View.</summary>
    public TestActorBuilder Holding(params AppSystemPermission[] permissions)
    {
        _system.AddRange(permissions);
        return this;
    }

    /// <summary>
    /// A team permission: a claim on <paramref name="teamId"/> (a team of its own in a View of its own when
    /// null) carrying it, with the actor a member of the team. A team not yet in a View gets one.
    /// </summary>
    public TestActorBuilder Holding(AppTeamPermission permission, Guid? teamId = null)
    {
        var team = teamId ?? Guid.NewGuid();
        _team.Add((team, permission));
        _memberships.Add(team);
        return this;
    }

    /// <summary>
    /// A View permission: carried on the claim of one of the actor's own teams in <paramref name="viewId"/>
    /// (its primary team there, else a team it is a member of, else a team of its own added to the View),
    /// which is where player.api reports a View permission. A View of its own when null.
    /// </summary>
    public TestActorBuilder Holding(AppViewPermission permission, Guid? viewId = null)
    {
        _view.Add((viewId, permission));
        return this;
    }

    /// <summary>Places <paramref name="teamId"/> in <paramref name="viewId"/>, for every caller.</summary>
    public TestActorBuilder InView(Guid teamId, Guid viewId)
    {
        _placements.Add((teamId, viewId));
        return this;
    }

    /// <summary>
    /// Makes <paramref name="teamId"/> the actor's primary team in its View (placed earlier, or by
    /// <see cref="InView"/>). Without it the actor's first membership in a View is its primary team there,
    /// as player.api makes it. Visibility comes from the primary team's direct permissions: <c>ViewView</c> or
    /// <c>ManageView</c> on it sees every team of the View, <c>ViewTeam</c> or <c>ManageTeam</c> the teams
    /// scoped onto it (<see cref="SeesScoped"/>), anything else the primary team alone.
    /// </summary>
    public TestActorBuilder PrimaryOn(Guid teamId)
    {
        _primary.Add(teamId);
        _memberships.Add(teamId);
        return this;
    }

    /// <summary>Makes the actor a member of <paramref name="teamId"/>, with no permission on it.</summary>
    public TestActorBuilder MemberOf(Guid teamId)
    {
        _member.Add(teamId);
        _memberships.Add(teamId);
        return this;
    }

    /// <summary>
    /// A claim on <paramref name="teamId"/> scoped in from the actor's primary team in the same View, carrying
    /// that membership's effective permissions and none of its own (no direct ones): the shape player.api
    /// reports for a team that shares itself with another (<c>UserClaimsService</c> accumulates the
    /// granting membership's permissions onto the target team).
    /// </summary>
    public TestActorBuilder SeesScoped(Guid teamId)
    {
        _scoped.Add(teamId);
        return this;
    }

    /// <summary>The actor, with its player.api data written to the directory.</summary>
    public Task<TestActor> SeedAsync()
    {
        ct.ThrowIfCancellationRequested();

        foreach (var (team, view) in _placements)
        {
            player.PlaceTeam(team, view);
        }

        var caller = player.Caller(_id);
        caller.System.UnionWith(_system);

        foreach (var team in _primary)
        {
            var claim = caller.ClaimOn(Placed(team));
            claim.Primary = true;
            claim.Member = true;
        }

        foreach (var team in _member)
        {
            caller.ClaimOn(Placed(team)).Member = true;
        }

        foreach (var (team, permission) in _team)
        {
            var claim = caller.ClaimOn(Placed(team.Value));
            claim.Member = true;
            claim.Grant(permission.ToString());
        }

        foreach (var (view, permission) in _view)
        {
            OwnTeamIn(caller, view ?? Guid.NewGuid()).Grant(permission.ToString());
        }

        // player.api makes a user's first membership in a View its primary team there: the first in the
        // order the steps named them, then any the builder minted for a View permission.
        var order = _memberships.Concat(caller.Claims.Keys).Distinct().ToList();

        foreach (var viewClaims in caller.Claims
            .Where(x => x.Value.Member)
            .OrderBy(x => order.IndexOf(x.Key))
            .GroupBy(x => player.ViewOf(x.Key)))
        {
            if (!caller.Claims.Any(x => x.Value.Primary && player.ViewOf(x.Key) == viewClaims.Key))
            {
                viewClaims.First().Value.Primary = true;
            }
        }

        foreach (var team in _scoped)
        {
            var viewId = player.ViewOf(Placed(team)).Value;
            var primary = caller.Claims.FirstOrDefault(x => x.Value.Primary && player.ViewOf(x.Key) == viewId);

            if (primary.Value is null)
            {
                throw new InvalidOperationException($"SeesScoped({team}) needs a team of the actor's in the same View.");
            }

            // A scoped claim carries the effective permissions of the membership that grants it.
            var scoped = caller.ClaimOn(team);
            scoped.Sources.Add(primary.Key);
            scoped.Permissions.UnionWith(primary.Value.Permissions);
        }

        return Task.FromResult(new TestActor { Id = _id, Name = _name });
    }

    /// <summary>The team, placed in a View of its own if no step or arrangement placed it.</summary>
    private Guid Placed(Guid teamId)
    {
        if (player.ViewOf(teamId) is null)
        {
            player.PlaceTeam(teamId, Guid.NewGuid());
        }

        return teamId;
    }

    /// <summary>The actor's primary team in the View, else a team it is a member of, else a new one.</summary>
    private PlayerClaim OwnTeamIn(PlayerCaller caller, Guid viewId)
    {
        var own = caller.Claims
            .Where(x => player.ViewOf(x.Key) == viewId && x.Value.Member)
            .OrderByDescending(x => x.Value.Primary)
            .Select(x => x.Value)
            .FirstOrDefault();

        if (own is not null)
        {
            return own;
        }

        var teamId = Guid.NewGuid();
        player.PlaceTeam(teamId, viewId);

        var claim = caller.ClaimOn(teamId);
        claim.Member = true;
        return claim;
    }
}
