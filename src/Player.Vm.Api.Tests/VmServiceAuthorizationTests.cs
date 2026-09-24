// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;
using VmMapEntity = Player.Vm.Api.Domain.Models.VmMap;
// Spelled out because the test project has its own Infrastructure namespace, which wins over the
// application's when the name is written relatively.
using AppSystemPermission = Player.Vm.Api.Infrastructure.Authorization.AppSystemPermission;
using AppViewPermission = Player.Vm.Api.Infrastructure.Authorization.AppViewPermission;
using AppTeamPermission = Player.Vm.Api.Infrastructure.Authorization.AppTeamPermission;

namespace Player.Vm.Api.Tests;

/// <summary>
/// VmService is the layer that decides which VMs a caller may see and touch. Every endpoint on
/// VmController goes through it, and the endpoint tests run with AllowEverything(), so this is the only
/// place the refusing paths are exercised.
///
/// Two kinds of decision are covered. The gates are the easy half: a permission is missing, so the call
/// throws. The filtering is the half that leaks quietly - GetByViewIdAsync and GetByTeamIdAsync return
/// a list, and a caller who should not see a personal VM gets a list with one extra entry rather than an
/// error. So does a caller who should not know which teams a VM belongs to, which is what
/// MapVisibleCollection masks.
///
/// IPlayerService is substituted; its own interpretation of player.api's claims is covered by
/// PlayerServiceAuthorizationTests. The database is real, because the filtering is partly done in SQL.
/// </summary>
public class VmServiceAuthorizationTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly Guid Caller = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherUser = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IPlayerService _player = Substitute.For<IPlayerService>();
    private readonly INetworkService _networks = Substitute.For<INetworkService>();

    private VmService Service => new(Db, _player, Principal(Caller), TestMapper.Value, _networks);

    #region CanAccessVm

    // The not-found check comes first, so a caller probing for VMs that exist cannot tell "no such VM"
    // from "not yours" - both arrive as an exception, but only this one is a 404.
    [Fact]
    public async Task CanAccessVm_ForAMissingVm_IsNotFound()
    {
        await Assert.ThrowsAsync<EntityNotFoundException<Features.Vms.Vm>>(
            () => Service.CanAccessVm(null, Ct));
    }

    // Seeing a team is no longer enough: reaching its Vms takes one of the Vm permissions.
    [Fact]
    public async Task CanAccessVm_WithoutVmAccessToItsTeams_IsForbidden()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()]);
        CanViewVms(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.CanAccessVm(vm, Ct));
    }

    [Fact]
    public async Task CanAccessVm_ForASharedVmInATeamWhoseVmsAreVisible_IsAllowed()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()]);
        CanViewVms(true);

        Assert.True(await Service.CanAccessVm(vm, Ct));
    }

    /// <summary>
    /// A personal VM belongs to one user. A team-scoped Vm permission is not enough to reach someone
    /// else's, which is what keeps one student out of another's workstation.
    /// </summary>
    [Fact]
    public async Task CanAccessVm_ForAnotherUsersPersonalVm_IsForbidden()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()], userId: OtherUser);
        CanViewVms(true);
        Can(false);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Service.CanAccessVm(vm, Ct));

        Assert.Contains("belongs to another user", ex.Message);
    }

    // The caller's own personal VM needs nothing beyond the team-scoped Vm permission - no elevated
    // permission is consulted at all, which is what the DidNotReceive pins.
    [Fact]
    public async Task CanAccessVm_ForTheCallersOwnPersonalVm_IsAllowedWithoutEscalation()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()], userId: Caller);
        CanViewVms(true);

        Assert.True(await Service.CanAccessVm(vm, Ct));

        await _player.DidNotReceive().Can(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<AppSystemPermission[]>(),
            Arg.Any<AppViewPermission[]>(),
            Arg.Any<AppTeamPermission[]>(),
            Arg.Any<CancellationToken>());
    }

    // An instructor or view admin does reach another user's personal VM - that is what the elevated
    // permission is for, and how the VM console shows up in a view-wide list.
    [Fact]
    public async Task CanAccessVm_ForAnotherUsersPersonalVm_IsAllowedWithViewPermission()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()], userId: OtherUser);
        CanViewVms(true);
        Can(true);

        Assert.True(await Service.CanAccessVm(vm, Ct));
    }

    #endregion

    #region GetByTeamIdAsync

    // A team the caller cannot see at all is refused rather than answered with an empty list.
    [Fact]
    public async Task GetByTeamId_ForATeamOutsideTheVisibilitySet_IsForbidden()
    {
        var teamId = Guid.NewGuid();
        Visibility(teamId, VisibilityFor());

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.GetByTeamIdAsync(teamId, null, false, false, Ct));
    }

    /// <summary>
    /// The second gate, and the one team visibility does not answer: the team is visible, but nothing
    /// grants the caller sight of its Vms. Refused rather than answered with an empty list, so the two
    /// are told apart in the same way as an invisible team.
    /// </summary>
    [Fact]
    public async Task GetByTeamId_WithoutVmAccessToTheTeam_IsForbidden()
    {
        var teamId = Guid.NewGuid();
        await Seed(Vm(teamIds: [teamId]));
        Visibility(teamId, VisibilityFor(teamId));
        CanViewVms(false);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.GetByTeamIdAsync(teamId, null, false, false, Ct));
    }

    // Personal VMs are opt-in. The default list is the shared machines, which is what the VM list in a
    // view shows before anyone asks for personal ones.
    [Fact]
    public async Task GetByTeamId_ByDefault_ExcludesPersonalVms()
    {
        var teamId = Guid.NewGuid();
        var shared = Vm(teamIds: [teamId], name: "shared");
        var mine = Vm(teamIds: [teamId], name: "mine", userId: Caller);
        await Seed(shared, mine);
        Visibility(teamId, VisibilityFor(teamId));
        CanViewVms(true);

        var vms = await Service.GetByTeamIdAsync(teamId, null, includePersonal: false, onlyMine: false, Ct);

        Assert.Equal<Guid>([shared.Id], vms.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task GetByTeamId_OnlyMine_ReturnsOnlyTheCallersPersonalVms()
    {
        var teamId = Guid.NewGuid();
        var shared = Vm(teamIds: [teamId], name: "shared");
        var mine = Vm(teamIds: [teamId], name: "mine", userId: Caller);
        var theirs = Vm(teamIds: [teamId], name: "theirs", userId: OtherUser);
        await Seed(shared, mine, theirs);
        Visibility(teamId, VisibilityFor(teamId));
        CanViewVms(true);

        var vms = await Service.GetByTeamIdAsync(teamId, null, includePersonal: false, onlyMine: true, Ct);

        Assert.Equal<Guid>([mine.Id], vms.Select(x => x.Id).ToArray());
    }

    /// <summary>
    /// The leak this guards. includePersonal widens the query to every personal VM on the team, and the
    /// caller's own visibility is what narrows it back down again in memory. Without CanViewAllTeams,
    /// another user's personal VM has to be dropped.
    /// </summary>
    [Fact]
    public async Task GetByTeamId_IncludePersonal_HidesAnotherUsersVmFromAnOrdinaryCaller()
    {
        var teamId = Guid.NewGuid();
        var shared = Vm(teamIds: [teamId], name: "shared");
        var mine = Vm(teamIds: [teamId], name: "mine", userId: Caller);
        var theirs = Vm(teamIds: [teamId], name: "theirs", userId: OtherUser);
        await Seed(shared, mine, theirs);
        Visibility(teamId, VisibilityFor(teamId, canViewAllTeams: false));
        CanViewVms(true);

        var vms = await Service.GetByTeamIdAsync(teamId, null, includePersonal: true, onlyMine: false, Ct);

        Assert.Equal<Guid>([mine.Id, shared.Id], vms.Select(x => x.Id).OrderBy(x => x != mine.Id).ToArray());
        Assert.DoesNotContain(theirs.Id, vms.Select(x => x.Id));
    }

    [Fact]
    public async Task GetByTeamId_IncludePersonal_ShowsEveryUsersVmToAViewWideCaller()
    {
        var teamId = Guid.NewGuid();
        var mine = Vm(teamIds: [teamId], name: "mine", userId: Caller);
        var theirs = Vm(teamIds: [teamId], name: "theirs", userId: OtherUser);
        await Seed(mine, theirs);
        Visibility(teamId, VisibilityFor(teamId, canViewAllTeams: true));
        CanViewVms(true);

        var vms = await Service.GetByTeamIdAsync(teamId, null, includePersonal: true, onlyMine: false, Ct);

        Assert.Contains(theirs.Id, vms.Select(x => x.Id));
    }

    /// <summary>
    /// A VM can be on several teams, and the caller learns only about the ones they can see. The front
    /// end groups by team id, so an unmasked list would disclose the existence of teams the caller has
    /// no access to.
    /// </summary>
    [Fact]
    public async Task GetByTeamId_MasksTeamIdsTheCallerCannotSee()
    {
        var visibleTeam = Guid.NewGuid();
        var hiddenTeam = Guid.NewGuid();
        var vm = Vm(teamIds: [visibleTeam, hiddenTeam], name: "shared");
        await Seed(vm);
        Visibility(visibleTeam, VisibilityFor(visibleTeam));
        CanViewVms(true);

        var vms = await Service.GetByTeamIdAsync(visibleTeam, null, false, false, Ct);

        Assert.Equal<Guid>([visibleTeam], vms.Single().TeamIds.ToArray());
    }

    [Fact]
    public async Task GetByTeamId_FiltersByName()
    {
        var teamId = Guid.NewGuid();
        var wanted = Vm(teamIds: [teamId], name: "wanted");
        var other = Vm(teamIds: [teamId], name: "other");
        await Seed(wanted, other);
        Visibility(teamId, VisibilityFor(teamId));
        CanViewVms(true);

        var vms = await Service.GetByTeamIdAsync(teamId, "wanted", false, false, Ct);

        Assert.Equal<Guid>([wanted.Id], vms.Select(x => x.Id).ToArray());
    }

    #endregion

    #region GetByViewIdAsync

    /// <summary>
    /// An empty list, not an exception. A View this caller has no teams in is indistinguishable from one
    /// with no VMs, and the workstation app polls this endpoint continuously.
    /// </summary>
    [Fact]
    public async Task GetByViewId_ForAViewWithNoTeams_IsEmpty()
    {
        var viewId = Guid.NewGuid();
        _player.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>()).Returns(VisibilityFor());
        _player.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>()).Returns((IEnumerable<Player.Api.Client.Team>)null);

        Assert.Empty(await Service.GetByViewIdAsync(viewId, null, false, false, Ct));
    }

    [Fact]
    public async Task GetByViewId_ByDefault_ExcludesPersonalVms()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var shared = Vm(teamIds: [teamId], name: "shared");
        var mine = Vm(teamIds: [teamId], name: "mine", userId: Caller);
        await Seed(shared, mine);
        View(viewId, VisibilityFor(teamId), teamId);
        CanViewVms(true);

        var vms = await Service.GetByViewIdAsync(viewId, null, includePersonal: false, onlyMine: false, Ct);

        Assert.Equal<Guid>([shared.Id], vms.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task GetByViewId_IncludePersonal_HidesAnotherUsersVmFromAnOrdinaryCaller()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var mine = Vm(teamIds: [teamId], name: "mine", userId: Caller);
        var theirs = Vm(teamIds: [teamId], name: "theirs", userId: OtherUser);
        await Seed(mine, theirs);
        View(viewId, VisibilityFor(teamId, canViewAllTeams: false), teamId);
        CanViewVms(true);

        var vms = await Service.GetByViewIdAsync(viewId, null, includePersonal: true, onlyMine: false, Ct);

        Assert.Equal<Guid>([mine.Id], vms.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task GetByViewId_IncludePersonal_ShowsEveryUsersVmToAViewWideCaller()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var mine = Vm(teamIds: [teamId], name: "mine", userId: Caller);
        var theirs = Vm(teamIds: [teamId], name: "theirs", userId: OtherUser);
        await Seed(mine, theirs);
        View(viewId, VisibilityFor(teamId, canViewAllTeams: true), teamId);
        CanViewVms(true);

        var vms = await Service.GetByViewIdAsync(viewId, null, includePersonal: true, onlyMine: false, Ct);

        Assert.Contains(theirs.Id, vms.Select(x => x.Id));
    }

    /// <summary>
    /// onlyMine takes a different query path from the rest of this method, and it is the one the
    /// workstation app uses. It must still return only the caller's own machines.
    /// </summary>
    [Fact]
    public async Task GetByViewId_OnlyMine_ReturnsOnlyTheCallersPersonalVms()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var mine = Vm(teamIds: [teamId], name: "mine", userId: Caller);
        var theirs = Vm(teamIds: [teamId], name: "theirs", userId: OtherUser);
        var shared = Vm(teamIds: [teamId], name: "shared");
        await Seed(mine, theirs, shared);
        View(viewId, VisibilityFor(teamId), teamId);
        CanViewVms(true);

        var vms = await Service.GetByViewIdAsync(viewId, null, includePersonal: false, onlyMine: true, Ct);

        Assert.Equal<Guid>([mine.Id], vms.Select(x => x.Id).ToArray());
    }

    /// <summary>
    /// With more than one personal VM, the one on the caller's primary team comes first. The workstation
    /// app reads only the first result, so this ordering is load-bearing rather than cosmetic.
    /// </summary>
    /// <remarks>
    /// The query behind this has no ORDER BY of its own, so what reaches the reordering step is whatever
    /// PostgreSQL hands back, in an order this test cannot pin down. Both insertion orders are run
    /// because whichever one already arrives with the primary team's VM first would pass with the
    /// reordering deleted; only the other one can catch that.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetByViewId_OnlyMine_PutsThePrimaryTeamsVmFirst(bool seedPrimaryFirst)
    {
        var viewId = Guid.NewGuid();
        var primaryTeam = Guid.NewGuid();
        var secondaryTeam = Guid.NewGuid();

        var onSecondary = Vm(teamIds: [secondaryTeam], name: "a-secondary", userId: Caller);
        var onPrimary = Vm(teamIds: [primaryTeam], name: "z-primary", userId: Caller);

        // Separate saves: one SaveChanges leaves the order of two inserts of the same type to EF.
        foreach (var vm in seedPrimaryFirst ? new[] { onPrimary, onSecondary } : [onSecondary, onPrimary])
        {
            await Seed(vm);
        }

        View(
            viewId,
            VisibilityFor(primaryTeam, secondaryTeam),
            teams: [Team(secondaryTeam), Team(primaryTeam, isPrimary: true)]);
        CanViewVms(true);

        var vms = await Service.GetByViewIdAsync(viewId, null, false, onlyMine: true, Ct);

        Assert.Equal(onPrimary.Id, vms.First().Id);
    }

    /// <summary>
    /// A View-wide list is an empty list rather than a refusal, because a View the caller has no Vm
    /// access in is indistinguishable from one with no Vms - the same choice the no-teams case makes.
    /// </summary>
    [Fact]
    public async Task GetByViewId_WithoutVmAccessToAnyTeam_IsEmpty()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        await Seed(Vm(teamIds: [teamId], name: "shared"));
        View(viewId, VisibilityFor(teamId), teamId);
        CanViewVms(false);

        Assert.Empty(await Service.GetByViewIdAsync(viewId, null, false, false, Ct));
    }

    /// <summary>
    /// Vm access is per team, so a caller scoped onto one team's Vms sees that team's machines and not
    /// the rest of the View's - even though every team here is visible to them.
    /// </summary>
    [Fact]
    public async Task GetByViewId_ExcludesTheVmsOfTeamsTheCallerHasNoVmAccessTo()
    {
        var viewId = Guid.NewGuid();
        var allowedTeam = Guid.NewGuid();
        var deniedTeam = Guid.NewGuid();

        var allowed = Vm(teamIds: [allowedTeam], name: "allowed");
        var denied = Vm(teamIds: [deniedTeam], name: "denied");
        await Seed(allowed, denied);

        View(viewId, VisibilityFor(false, [allowedTeam, deniedTeam]), teams: [Team(allowedTeam), Team(deniedTeam)]);
        CanViewVmsOnly(allowedTeam);

        var vms = await Service.GetByViewIdAsync(viewId, null, false, false, Ct);

        Assert.Equal<Guid>([allowed.Id], vms.Select(x => x.Id).ToArray());
    }

    /// <summary>
    /// A View-level Vm grant covers every team in the View at once, so the per-team checks are skipped
    /// entirely - otherwise a large View would repeat the same lookup once per team.
    /// </summary>
    [Fact]
    public async Task GetByViewId_WithAViewLevelVmGrant_SkipsThePerTeamChecks()
    {
        var viewId = Guid.NewGuid();
        var firstTeam = Guid.NewGuid();
        var secondTeam = Guid.NewGuid();

        var first = Vm(teamIds: [firstTeam], name: "first");
        var second = Vm(teamIds: [secondTeam], name: "second");
        await Seed(first, second);

        View(viewId, VisibilityFor(false, [firstTeam, secondTeam]), teams: [Team(firstTeam), Team(secondTeam)]);
        CanViewVms(false);
        _player.CanViewVms(
            Arg.Is<IEnumerable<Guid>>(ids => !ids.Any()),
            Arg.Is<IEnumerable<Guid>>(ids => ids != null && ids.Contains(viewId)),
            Arg.Any<CancellationToken>()).Returns(true);

        var vms = await Service.GetByViewIdAsync(viewId, null, false, false, Ct);

        Assert.Equal(new HashSet<Guid> { first.Id, second.Id }, vms.Select(x => x.Id).ToHashSet());
        await _player.DidNotReceive().CanViewVms(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Any()),
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByViewId_MasksTeamIdsTheCallerCannotSee()
    {
        var viewId = Guid.NewGuid();
        var visibleTeam = Guid.NewGuid();
        var hiddenTeam = Guid.NewGuid();
        var vm = Vm(teamIds: [visibleTeam, hiddenTeam], name: "shared");
        await Seed(vm);

        // Both teams are in the View, so both reach the query - only visibility narrows the result.
        View(viewId, VisibilityFor(visibleTeam), teams: [Team(visibleTeam), Team(hiddenTeam)]);
        CanViewVms(true);

        var vms = await Service.GetByViewIdAsync(viewId, null, false, false, Ct);

        Assert.Equal<Guid>([visibleTeam], vms.Single().TeamIds.ToArray());
    }

    #endregion

    #region System-wide reads

    // GetAllAsync and GetAllMapsAsync have no team to scope to, so they are gated on a system
    // permission outright. These are the endpoints an administrative UI uses.
    [Fact]
    public async Task GetAll_WithoutASystemPermission_IsForbidden()
    {
        CanViewVms(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetAllAsync(Ct));
    }

    [Fact]
    public async Task GetAllMaps_WithoutASystemPermission_IsForbidden()
    {
        CanViewMaps(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetAllMapsAsync(Ct));
    }

    [Fact]
    public async Task GetAll_WithASystemPermission_ReturnsEveryVm()
    {
        await Seed(Vm(teamIds: [Guid.NewGuid()]), Vm(teamIds: [Guid.NewGuid()]));
        CanViewVms(true);

        Assert.Equal(2, (await Service.GetAllAsync(Ct)).Length);
    }

    #endregion

    #region Maps

    /// <summary>
    /// A Map assigned to teams takes a Map permission, and belonging to the View is not a substitute -
    /// the membership fallback below is for teamless Maps only, so it is stubbed true here to prove it
    /// does not reach this one.
    /// </summary>
    [Fact]
    public async Task GetMap_WithoutMapAccessToItsTeams_IsForbidden()
    {
        var map = Map(teamIds: [Guid.NewGuid()]);
        await Seed(map);
        CanViewMaps(false);
        IsInView(true);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetMapAsync(map.Id, Ct));
    }

    /// <summary>
    /// A Map assigned to no team belongs to the View as a whole, so everyone in the View may read it
    /// without holding any Map permission. That is a deliberate exception, not the old hole: the old
    /// behaviour skipped the check for anyone authenticated, including callers with no claim on the View
    /// at all, which the next test pins as refused.
    /// </summary>
    [Fact]
    public async Task GetMap_WithNoTeams_IsReadableByAnyoneInTheView()
    {
        var map = Map(teamIds: []);
        await Seed(map);
        CanViewMaps(false);
        IsInView(true);

        Assert.NotNull(await Service.GetMapAsync(map.Id, Ct));

        await _player.Received().IsInViewAsync(map.ViewId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMap_WithNoTeams_IsForbiddenForACallerOutsideTheView()
    {
        var map = Map(teamIds: []);
        await Seed(map);
        CanViewMaps(false);
        IsInView(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetMapAsync(map.Id, Ct));
    }

    // The other way in: a Map permission held at the View or system level reaches a teamless Map even
    // without membership of the View, which is how an administrative UI lists it.
    [Fact]
    public async Task GetMap_WithNoTeams_IsAllowedByAViewLevelMapPermission()
    {
        var map = Map(teamIds: []);
        await Seed(map);
        CanViewMaps(true);
        IsInView(false);

        Assert.NotNull(await Service.GetMapAsync(map.Id, Ct));

        await _player.Received().CanViewMaps(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(map.ViewId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTeamMap_WithoutMapAccessToTheTeam_IsForbidden()
    {
        CanViewMaps(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetTeamMapAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task DeleteMap_WithoutMapManagementOnItsTeams_IsForbidden()
    {
        var map = Map(teamIds: [Guid.NewGuid()]);
        await Seed(map);
        CanManageMaps(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.DeleteMapAsync(map.Id, Ct));
    }

    // The counterpart of the teamless-read case: a Map belonging to the View as a whole takes a
    // View-level grant to delete, where before it took nothing at all.
    [Fact]
    public async Task DeleteMap_WithNoTeams_IsForbiddenWithoutAViewLevelMapPermission()
    {
        var map = Map(teamIds: []);
        await Seed(map);
        CanManageMaps(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.DeleteMapAsync(map.Id, Ct));
    }

    /// <summary>
    /// null, so the controller answers 404. An unknown View must not read as a View with no maps, which
    /// is why GetViewMapsAsync probes the teams endpoint even though it filters on the Map permissions.
    /// </summary>
    [Fact]
    public async Task GetViewMaps_ForAnUnknownView_IsNull()
    {
        var viewId = Guid.NewGuid();
        _player.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>()).Returns(VisibilityFor());
        _player.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>()).Returns((IEnumerable<Player.Api.Client.Team>)null);

        Assert.Null(await Service.GetViewMapsAsync(viewId, Ct));
    }

    [Fact]
    public async Task GetViewMaps_ReturnsOnlyTheMapsTheCallerCanView()
    {
        var viewId = Guid.NewGuid();
        var allowedTeam = Guid.NewGuid();
        var deniedTeam = Guid.NewGuid();

        var allowed = Map(teamIds: [allowedTeam], viewId: viewId);
        var denied = Map(teamIds: [deniedTeam], viewId: viewId);
        await Seed(allowed, denied);

        View(viewId, VisibilityFor(false, [allowedTeam, deniedTeam]), teams: [Team(allowedTeam), Team(deniedTeam)]);
        CanViewMapsOnly(allowedTeam);
        IsInView(true);

        var maps = await Service.GetViewMapsAsync(viewId, Ct);

        Assert.Equal<Guid>([allowed.Id], maps.Select(x => x.Id).ToArray());
    }

    /// <summary>
    /// The listing counterpart of <see cref="GetMap_WithNoTeams_IsReadableByAnyoneInTheView"/>. A View
    /// member with no Map permission at all still sees the View's teamless Maps, and still does not see
    /// the ones scoped to a team.
    /// </summary>
    [Fact]
    public async Task GetViewMaps_IncludesTheTeamlessMapsForAnyViewMember()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var teamless = Map(teamIds: [], viewId: viewId);
        var teamScoped = Map(teamIds: [teamId], viewId: viewId);
        await Seed(teamless, teamScoped);

        View(viewId, VisibilityFor(teamId), teams: [Team(teamId)]);
        CanViewMaps(false);
        IsInView(true);

        var maps = await Service.GetViewMapsAsync(viewId, Ct);

        Assert.Equal<Guid>([teamless.Id], maps.Select(x => x.Id).ToArray());
    }

    /// <summary>
    /// Every Map in the listing is in the same View, so a View-level Map grant answers for all of them
    /// and neither the membership check nor any per-Map check is needed.
    /// </summary>
    [Fact]
    public async Task GetViewMaps_WithAViewLevelMapGrant_SkipsThePerMapChecks()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var teamless = Map(teamIds: [], viewId: viewId);
        var teamScoped = Map(teamIds: [teamId], viewId: viewId);
        await Seed(teamless, teamScoped);

        View(viewId, VisibilityFor(teamId), teams: [Team(teamId)]);
        CanViewMaps(false);
        _player.CanViewMaps(
            Arg.Is<IEnumerable<Guid>>(ids => !ids.Any()),
            Arg.Is<IEnumerable<Guid>>(ids => ids != null && ids.Contains(viewId)),
            Arg.Any<CancellationToken>()).Returns(true);

        var maps = await Service.GetViewMapsAsync(viewId, Ct);

        Assert.Equal(new HashSet<Guid> { teamless.Id, teamScoped.Id }, maps.Select(x => x.Id).ToHashSet());
        await _player.DidNotReceive().IsInViewAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _player.DidNotReceive().CanViewMaps(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Any()),
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>());
    }

    // Membership is a property of the View, not of any one Map, so it is asked once for the listing.
    [Fact]
    public async Task GetViewMaps_AsksForViewMembershipOnce()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        await Seed(Map(teamIds: [], viewId: viewId), Map(teamIds: [], viewId: viewId), Map(teamIds: [], viewId: viewId));

        View(viewId, VisibilityFor(teamId), teams: [Team(teamId)]);
        CanViewMaps(false);
        IsInView(true);

        Assert.Equal(3, (await Service.GetViewMapsAsync(viewId, Ct)).Length);
        await _player.Received(1).IsInViewAsync(viewId, Arg.Any<CancellationToken>());
    }

    // A map can only be assigned to teams the caller manages, and the view must exist. Both failures are
    // flattened into ForbiddenException by validateViewAndTeams, including the not-found case.
    [Fact]
    public async Task CreateMap_ForAViewThatDoesNotExist_IsForbidden()
    {
        _player.GetViewByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Player.Api.Client.View>(_ => throw new InvalidOperationException("no such view"));

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [] }, Guid.NewGuid(), Ct));

        Assert.Contains("View does not exist", ex.Message);
    }

    [Fact]
    public async Task CreateMap_ForATeamInAnotherView_IsForbidden()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        _player.GetViewByIdAsync(viewId, Arg.Any<CancellationToken>()).Returns(new Player.Api.Client.View());
        _player.GetTeamById(teamId).Returns(new Player.Api.Client.Team { Id = teamId, ViewId = Guid.NewGuid() });

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [teamId] }, viewId, Ct));

        Assert.Contains("is not a member of the specified view", ex.Message);
    }

    [Fact]
    public async Task CreateMap_WithoutMapManagementOnTheTeams_IsForbidden()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        _player.GetViewByIdAsync(viewId, Arg.Any<CancellationToken>()).Returns(new Player.Api.Client.View());
        _player.GetTeamById(teamId).Returns(new Player.Api.Client.Team { Id = teamId, ViewId = viewId });
        CanManageMaps(false);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [teamId] }, viewId, Ct));
    }

    /// <summary>
    /// Creating a Map with no teams used to be the one write validateViewAndTeams waved through with no
    /// permission check at all - and the resulting Map was then readable View-wide. It now takes the
    /// View-level Map permission, asked for with the View id and no teams.
    /// </summary>
    [Fact]
    public async Task CreateMap_WithNoTeams_IsForbiddenWithoutAViewLevelMapPermission()
    {
        var viewId = Guid.NewGuid();
        _player.GetViewByIdAsync(viewId, Arg.Any<CancellationToken>()).Returns(new Player.Api.Client.View());
        CanManageMaps(false);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [] }, viewId, Ct));
    }

    [Fact]
    public async Task CreateMap_WithNoTeams_IsAllowedByAViewLevelMapPermission()
    {
        var viewId = Guid.NewGuid();
        _player.GetViewByIdAsync(viewId, Arg.Any<CancellationToken>()).Returns(new Player.Api.Client.View());
        CanManageMaps(true);

        var map = await Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [] }, viewId, Ct);

        Assert.Equal(viewId, map.ViewId);

        await _player.Received().CanManageMaps(
            Arg.Is<IEnumerable<Guid>>(ids => !ids.Any()),
            Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(viewId)),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Mutating a Vm

    /// <summary>
    /// Every way of changing a VM or its team membership is gated on manage rights over the teams
    /// involved. Driven as a theory so that adding a mutating method without a gate shows up as a
    /// missing case here rather than as nothing at all.
    /// </summary>
    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("addToTeam")]
    [InlineData("removeFromTeam")]
    public async Task Mutation_WithoutManageOnTheVmsTeams_IsForbidden(string operation)
    {
        var teamId = Guid.NewGuid();
        var vm = Vm(teamIds: [teamId]);
        await Seed(vm);
        CanManageTeams(false);

        var service = Service;

        Task act = operation switch
        {
            "update" => service.UpdateAsync(vm.Id, new VmUpdateForm { Name = "renamed" }, Ct),
            "delete" => service.DeleteAsync(vm.Id, Ct),
            "addToTeam" => service.AddToTeamAsync(vm.Id, Guid.NewGuid(), Ct),
            "removeFromTeam" => service.RemoveFromTeamAsync(vm.Id, teamId, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "unhandled operation")
        };

        await Assert.ThrowsAsync<ForbiddenException>(() => act);
    }

    [Fact]
    public async Task Create_WithoutManageOnTheRequestedTeams_IsForbidden()
    {
        CanManageTeams(false);

        var form = new VmCreateForm { Id = Guid.NewGuid(), Name = "new", TeamIds = [Guid.NewGuid()] };

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.CreateAsync(form, Ct));
    }

    // A VM with no team would be unreachable by any team-scoped permission check, so it is refused
    // before the manage check rather than becoming an orphan only an administrator could see.
    [Fact]
    public async Task Create_WithNoTeams_IsForbidden()
    {
        CanManageTeams(true);

        var form = new VmCreateForm { Id = Guid.NewGuid(), Name = "new", TeamIds = [] };

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Service.CreateAsync(form, Ct));

        Assert.Contains("at least 1 team", ex.Message);
    }

    [Fact]
    public async Task Create_WithAnIdThatAlreadyExists_IsForbidden()
    {
        var existing = Vm(teamIds: [Guid.NewGuid()]);
        await Seed(existing);
        CanManageTeams(true);

        var form = new VmCreateForm { Id = existing.Id, Name = "duplicate", TeamIds = [Guid.NewGuid()] };

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Service.CreateAsync(form, Ct));

        Assert.Contains("already exists", ex.Message);
    }

    /// <summary>
    /// The same reason a VM cannot be created without a team: removing the last one would strand it.
    /// This fires after the manage check, so it is a real rule rather than a permission failure.
    /// </summary>
    [Fact]
    public async Task RemoveFromTeam_WhenItIsTheOnlyTeam_IsForbidden()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm(teamIds: [teamId]);
        await Seed(vm);
        CanManageTeams(true);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.RemoveFromTeamAsync(vm.Id, teamId, Ct));

        Assert.Contains("at least one team", ex.Message);
    }

    [Fact]
    public async Task RemoveFromTeam_WithAnotherTeamRemaining_Succeeds()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var vm = Vm(teamIds: [first, second]);
        await Seed(vm);
        CanManageTeams(true);

        Assert.True(await Service.RemoveFromTeamAsync(vm.Id, first, Ct));

        await using var context = NewContext();
        Assert.Equal<Guid>([second], context.VmTeams.Where(x => x.VmId == vm.Id).Select(x => x.TeamId).ToArray());
    }

    #endregion

    #region Helpers

    private static ClaimsPrincipal Principal(Guid userId) =>
        new(new ClaimsIdentity([new Claim("sub", userId.ToString())], "test"));

    private void CanViewVms(bool allowed) =>
        _player.CanViewVms(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>()).Returns(allowed);

    private void CanViewMaps(bool allowed) =>
        _player.CanViewMaps(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>()).Returns(allowed);

    private void CanManageMaps(bool allowed) =>
        _player.CanManageMaps(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>()).Returns(allowed);

    /// <summary>Whether the caller belongs to the View - membership, not a permission.</summary>
    private void IsInView(bool inView) =>
        _player.IsInViewAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(inView);

    /// <summary>
    /// Grants Vm access to exactly these teams and refuses it for every other, which is what a
    /// team-scoped grant looks like from VmService's side.
    /// </summary>
    private void CanViewVmsOnly(params Guid[] teamIds)
    {
        CanViewVms(false);

        foreach (var teamId in teamIds)
        {
            _player.CanViewVms(
                Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(teamId)),
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<CancellationToken>()).Returns(true);
        }
    }

    /// <summary>The Map counterpart of <see cref="CanViewVmsOnly"/>.</summary>
    private void CanViewMapsOnly(params Guid[] teamIds)
    {
        CanViewMaps(false);

        foreach (var teamId in teamIds)
        {
            _player.CanViewMaps(
                Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(teamId)),
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<CancellationToken>()).Returns(true);
        }
    }

    private void CanManageTeams(bool allowed) =>
        _player.CanManageTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(allowed);

    private void Can(bool allowed) =>
        _player.Can(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<AppSystemPermission[]>(),
            Arg.Any<AppViewPermission[]>(),
            Arg.Any<AppTeamPermission[]>(),
            Arg.Any<CancellationToken>()).Returns(allowed);

    private void Visibility(Guid teamId, VisibilityContext context) =>
        _player.GetVisibilityContextForTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns(context);

    /// <summary>Wires the two calls GetByViewIdAsync and GetViewMapsAsync both make.</summary>
    private void View(Guid viewId, VisibilityContext visibility, params Guid[] teamIds) =>
        View(viewId, visibility, teamIds.Select(x => Team(x)).ToArray());

    private void View(Guid viewId, VisibilityContext visibility, Player.Api.Client.Team[] teams)
    {
        _player.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>()).Returns(visibility);
        _player.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>()).Returns(teams);
    }

    private static VisibilityContext VisibilityFor(params Guid[] teamIds) =>
        VisibilityFor(false, teamIds);

    private static VisibilityContext VisibilityFor(Guid teamId, bool canViewAllTeams) =>
        VisibilityFor(canViewAllTeams, [teamId]);

    private static VisibilityContext VisibilityFor(bool canViewAllTeams, Guid[] teamIds) =>
        teamIds.Length == 0
            ? VisibilityContext.Empty
            : new VisibilityContext(teamIds[0], canViewAllTeams, [.. teamIds]);

    private static Player.Api.Client.Team Team(Guid id, bool isPrimary = false) =>
        new() { Id = id, Name = $"team-{id}", IsPrimary = isPrimary };

    private static VmEntity Vm(Guid[] teamIds, string name = null, Guid? userId = null)
    {
        var id = Guid.NewGuid();

        return new VmEntity
        {
            Id = id,
            Name = name ?? $"vm-{id}",
            Type = VmType.Vsphere,
            UserId = userId,
            VmTeams = [.. teamIds.Select(teamId => new VmTeam(teamId, id))]
        };
    }

    private static VmMapEntity Map(Guid[] teamIds, Guid? viewId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "map",
            ViewId = viewId ?? Guid.NewGuid(),
            TeamIds = [.. teamIds]
        };

    #endregion
}
