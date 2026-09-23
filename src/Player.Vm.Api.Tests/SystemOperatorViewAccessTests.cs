// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Player.Api.Client;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;
using AppSystemPermission = Player.Vm.Api.Infrastructure.Authorization.AppSystemPermission;
// Spelled out because the application has a Team of its own, which wins over player.api's when the name is
// written relatively.
using PlayerTeam = Player.Api.Client.Team;

namespace Player.Vm.Api.Tests;

/// <summary>
/// A member of player.api's Administrators group hitting views/{id}/vms used to get an empty list. Nothing
/// refused them - the View simply looked like a View with no VMs, which is the failure mode worth a test of
/// its own because no error ever surfaced.
///
/// The cause spanned a seam, which is why this suite exists alongside the two it overlaps.
/// PlayerServiceAuthorizationTests substitutes the player.api client and VmServiceAuthorizationTests
/// substitutes IPlayerService, so each half looked correct in isolation: system permissions come from
/// Role.AllPermissions, which player.api expands over the system permission table only, while team
/// permission claims come purely from TeamMemberships. An operator who was never added to a team therefore
/// arrives with every system permission and no team claim at all - and every View-scoped read keyed off the
/// claim. These tests wire the real PlayerService to the real VmService so that seam is covered end to end.
/// </summary>
public class SystemOperatorViewAccessTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly Guid Operator = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly IPlayerApiClient _client = Substitute.For<IPlayerApiClient>();
    private readonly IViewService _viewService = Substitute.For<IViewService>();
    private readonly INetworkService _networks = Substitute.For<INetworkService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    /// <summary>
    /// The regression. Two teams, one VM each, an operator who is a member of neither - all of it visible.
    /// </summary>
    [Fact]
    public async Task GetByViewId_ForASystemOperatorWithNoMembership_ReturnsEveryVmInTheView()
    {
        var viewId = Guid.NewGuid();
        var (teamA, teamB) = (Guid.NewGuid(), Guid.NewGuid());
        var (first, second) = (Vm(teamA, "alpha"), Vm(teamB, "beta"));
        await Seed(first, second);

        NotAMemberOf(viewId, teamA, teamB);
        SystemPermissions(nameof(AppSystemPermission.ViewVms));

        var vms = await Service.GetByViewIdAsync(viewId, null, false, false, Ct);

        Assert.Equal<Guid>([first.Id, second.Id], vms.Select(x => x.Id).ToArray());
    }

    /// <summary>
    /// Team ids survive the trip. VmService masks the team ids the caller cannot see, and vm.ui groups the
    /// list by them - so an operator who got the VMs but no team ids would see one ungrouped heap.
    /// </summary>
    [Fact]
    public async Task GetByViewId_ForASystemOperator_KeepsTheTeamIdsTheUiGroupsBy()
    {
        var viewId = Guid.NewGuid();
        var teamA = Guid.NewGuid();
        await Seed(Vm(teamA, "alpha"));

        NotAMemberOf(viewId, teamA);
        SystemPermissions(nameof(AppSystemPermission.ViewVms));

        var vms = await Service.GetByViewIdAsync(viewId, null, false, false, Ct);

        Assert.Equal<Guid>([teamA], vms.Single().TeamIds.ToArray());
    }

    // The team list behind the View's grouping and filtering, empty for the same reason and fixed by the
    // same change.
    [Fact]
    public async Task GetTeams_ForASystemOperator_ReturnsTheWholeRoster()
    {
        var viewId = Guid.NewGuid();
        var (teamA, teamB) = (Guid.NewGuid(), Guid.NewGuid());

        NotAMemberOf(viewId, teamA, teamB);
        SystemPermissions(nameof(AppSystemPermission.ViewVms));

        var teams = await Service.GetTeamsAsync(viewId, Ct);

        Assert.Equal<Guid>([teamA, teamB], teams.Select(x => x.Id).ToArray());
    }

    /// <summary>
    /// The permission has to be one that reaches VMs. Someone who can merely look at every View is not
    /// thereby entitled to its consoles, so they still get the empty list.
    /// </summary>
    [Fact]
    public async Task GetByViewId_ForACallerWhoCanOnlySeeViews_IsStillEmpty()
    {
        var viewId = Guid.NewGuid();
        var teamA = Guid.NewGuid();
        await Seed(Vm(teamA, "alpha"));

        NotAMemberOf(viewId, teamA);
        SystemPermissions(nameof(AppSystemPermission.ViewViews));

        Assert.Empty(await Service.GetByViewIdAsync(viewId, null, false, false, Ct));
    }

    // Unchanged for everyone else: no permission, no VMs, and still a list rather than a refusal.
    [Fact]
    public async Task GetByViewId_ForACallerWithNothing_IsStillEmpty()
    {
        var viewId = Guid.NewGuid();
        var teamA = Guid.NewGuid();
        await Seed(Vm(teamA, "alpha"));

        NotAMemberOf(viewId, teamA);
        SystemPermissions();

        Assert.Empty(await Service.GetByViewIdAsync(viewId, null, false, false, Ct));
    }

    #region Helpers

    private VmService Service
    {
        get
        {
            var accessor = Substitute.For<IHttpContextAccessor>();
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Operator.ToString())]))
            };
            accessor.HttpContext.Returns(context);

            var player = new PlayerService(accessor, _client, _viewService, _cache);
            return new VmService(Db, player, context.User, TestMapper.Value, _networks);
        }
    }

    /// <summary>
    /// A View the caller has no membership in: player.api hands back no team permission claims and no
    /// teams of their own, exactly as it does for an account that was never added to a team. The roster
    /// comes from this service's own credentials instead, which is what IViewService wraps.
    /// </summary>
    private void NotAMemberOf(Guid viewId, params Guid[] teamIds)
    {
        _client.GetMyTeamPermissionsAsync(viewId, null, true, Arg.Any<CancellationToken>())
            .Returns(new List<TeamPermissionsClaim>());
        _client.GetUserViewTeamsAsync(viewId, Operator, Arg.Any<CancellationToken>())
            .Returns(new List<PlayerTeam>());

        _viewService.GetTeamsForView(viewId, Arg.Any<CancellationToken>()).Returns(teamIds.ToList());
        _viewService.GetTeamDetailsForView(viewId, Arg.Any<CancellationToken>())
            .Returns(teamIds.Select(x => new PlayerTeam { Id = x, Name = $"team-{x}", ViewId = viewId }).ToArray());

        foreach (var teamId in teamIds)
        {
            _viewService.GetViewIdForTeam(teamId, Arg.Any<CancellationToken>()).Returns(viewId);
        }
    }

    private void SystemPermissions(params string[] permissions) =>
        _client.GetMyPermissionsAsync(Arg.Any<CancellationToken>()).Returns(permissions.ToList());

    private static VmEntity Vm(Guid teamId, string name)
    {
        var id = Guid.NewGuid();

        return new VmEntity
        {
            Id = id,
            Name = name,
            Type = VmType.Vsphere,
            VmTeams = [new VmTeam(teamId, id)]
        };
    }

    #endregion
}
