// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using Microsoft.Extensions.Caching.Memory;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// The real <see cref="PlayerService"/> without a host, for the tests that construct a service directly:
/// a substituted <see cref="IPlayerApiClient"/> and <see cref="IViewService"/> answered by a
/// <see cref="PlayerDirectory"/> for one caller, arranged with <see cref="Caller"/> and <see cref="Actor"/>.
/// </summary>
/// <remarks>
/// <see cref="Service"/> is a new <c>PlayerService</c>, with a new cache, each time it is read: the real one
/// keeps a View's claims for its own lifetime and the caller's system permissions in its cache, so one built
/// before the arrangement would answer from before it.
/// </remarks>
public sealed class PlayerHarness
{
    private readonly CancellationToken _ct;
    private Guid _asking;

    public PlayerHarness(Guid callerId, CancellationToken ct, ClaimsPrincipal user = null)
    {
        CallerId = callerId;
        User = user ?? new ClaimsPrincipalBuilder().WithUserId(callerId).Build();
        _ct = ct;
        Directory = new PlayerDirectory(Client, Views, () => _asking);
        _asking = callerId;
    }

    /// <summary>The user every question is asked for.</summary>
    public Guid CallerId { get; }

    public ClaimsPrincipal User { get; }

    public IPlayerApiClient Client { get; } = Substitute.For<IPlayerApiClient>();

    /// <summary>Team-to-View lookups answered by <see cref="Directory"/>; the rest arranged per test.</summary>
    public IViewService Views { get; } = Substitute.For<IViewService>();

    public PlayerDirectory Directory { get; }

    /// <summary>A real <see cref="PlayerService"/> for <see cref="User"/>, built now.</summary>
    public PlayerService Service
    {
        get
        {
            _asking = CallerId;
            return AuthorizationHarness.CreatePlayerService(User, Client, Views, new MemoryCache(new MemoryCacheOptions()));
        }
    }

    /// <summary>
    /// A real <see cref="PlayerService"/> for another user, built now. The client answers for the user of
    /// the last service this harness built, so use one at a time.
    /// </summary>
    public PlayerService ServiceFor(Guid userId)
    {
        _asking = userId;
        return AuthorizationHarness.CreatePlayerService(
            new ClaimsPrincipalBuilder().WithUserId(userId).Build(), Client, Views, new MemoryCache(new MemoryCacheOptions()));
    }

    /// <summary>Describes the caller; the steps add to what it already holds.</summary>
    public TestActorBuilder Caller() => new TestActorBuilder(Directory, _ct).WithId(CallerId);

    /// <summary>
    /// Adds the steps to what player.api holds for the caller, now. The builder writes to the directory as
    /// it seeds, so its task is complete when it returns.
    /// </summary>
    public void Grant(Func<TestActorBuilder, TestActorBuilder> steps) =>
        steps(Caller()).SeedAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Writes the player.api data a <see cref="VisibilityContext"/> comes from, for the caller in
    /// <paramref name="viewId"/>, replacing the caller's claims there: the primary team (placed in the View
    /// if it is not); with <paramref name="canViewAllTeams"/>, <c>ViewView</c> on it, so every team of the
    /// View is visible (the first team of the View is primary when none is named); otherwise
    /// <c>ViewTeam</c> on it and each other team scoped onto it. No primary and no
    /// <paramref name="canViewAllTeams"/> is a caller who sees nothing there.
    /// </summary>
    public void Sees(Guid viewId, Guid? primaryTeamId, bool canViewAllTeams, IEnumerable<Guid> teamIds) =>
        Directory.Sees(CallerId, viewId, primaryTeamId, canViewAllTeams, teamIds);

    /// <summary>The same, from a <see cref="VisibilityContext"/> as a substituted service would have answered.</summary>
    public void Sees(Guid viewId, VisibilityContext context) =>
        Sees(viewId, context.PrimaryTeamId, context.CanViewAllTeams, context.TeamIds);

    /// <summary>Removes the caller's claims on the teams of <paramref name="viewId"/>.</summary>
    public void ForgetView(Guid viewId)
    {
        var caller = Directory.Caller(CallerId);

        foreach (var team in caller.Claims.Keys.Where(x => Directory.ViewOf(x) == viewId).ToArray())
            caller.Claims.Remove(team);
    }

    /// <summary>Describes another user, for a test about whose answer is whose.</summary>
    public TestActorBuilder Actor() => new(Directory, _ct);
}
