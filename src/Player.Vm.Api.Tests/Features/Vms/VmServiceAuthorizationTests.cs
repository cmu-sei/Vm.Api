// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Tests.Support;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;
using VmMapEntity = Player.Vm.Api.Domain.Models.VmMap;
// Spelled out because the test project has its own Infrastructure namespace, which wins over the
// application's when the name is written relatively.
using AppSystemPermission = Player.Vm.Api.Infrastructure.Authorization.AppSystemPermission;
using AppViewPermission = Player.Vm.Api.Infrastructure.Authorization.AppViewPermission;
using AppTeamPermission = Player.Vm.Api.Infrastructure.Authorization.AppTeamPermission;
using AppPermissions = Player.Vm.Api.Infrastructure.Authorization.AppPermissions;

namespace Player.Vm.Api.Tests.Features.Vms;

/// <summary>
/// VmService is the layer that decides which VMs a caller may see and touch. Every endpoint on
/// VmController goes through it; this class drives each of its refusing paths directly, and
/// VmsEndpointTests checks each gate once more over HTTP.
///
/// Two kinds of decision are covered. The gates are the easy half: a permission is missing, so the call
/// throws. The filtering is the half that leaks quietly - GetByViewIdAsync and GetByTeamIdAsync return
/// a list, and a caller who should not see a personal VM gets a list with one extra entry rather than an
/// error. So does a caller who should not know which teams a VM belongs to, which is what
/// MapVisibleCollection masks.
///
/// PlayerService is real, over a substituted player.api client answered by a PlayerDirectory (see
/// PlayerHarness), so every decision here is the one production makes from those answers. The database is
/// real, because the filtering is partly done in SQL.
/// </summary>
public class VmServiceAuthorizationTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly Guid Caller = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherUser = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly PlayerHarness _player = new(Caller, TestContext.Current.CancellationToken, Principal(Caller));
    private readonly INetworkService _networks = Substitute.For<INetworkService>();
    private PlayerServiceSpy _spy;

    private IViewService _views => _player.Views;

    /// <summary>A VmService over a new real PlayerService, built now; <see cref="_spy"/> records its questions.</summary>
    private VmService Service =>
        new(Db, PlayerServiceSpy.Wrap(_player.Service, out _spy), _views, Principal(Caller), TestMapper.Mapper, _networks);

    #region CanAccessVm

    // The not-found check comes first, so a caller probing for VMs that exist cannot tell "no such VM"
    // from "not yours" - both arrive as an exception, but only this one is a 404.
    [Fact]
    public async Task CanAccessVm_ForAMissingVm_IsNotFound()
    {
        await Assert.ThrowsAsync<EntityNotFoundException<Player.Vm.Api.Features.Vms.Vm>>(
            () => Service.CanAccessVm(null, Ct));
    }

    // Seeing a team is not enough: reaching its Vms takes one of the Vm permissions.
    [Fact]
    public async Task CanAccessVm_is_forbidden_for_a_caller_holding_only_ViewTeamMaps()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm(teamIds: [teamId]);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamMaps, teamId));

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.CanAccessVm(vm, Ct));
    }

    [Fact]
    public async Task CanAccessVm_is_allowed_for_a_caller_holding_exactly_ViewTeamVms_on_the_Vms_team()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm(teamIds: [teamId]);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamVms, teamId));

        Assert.True(await Service.CanAccessVm(vm, Ct));
    }

    [Fact]
    public async Task CanAccessVm_is_forbidden_for_a_caller_holding_ViewTeamVms_only_on_another_team()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()]);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamVms, Guid.NewGuid()));

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
        var teamId = Guid.NewGuid();
        var vm = Vm(teamIds: [teamId], userId: OtherUser);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamVms, teamId));

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Service.CanAccessVm(vm, Ct));

        Assert.Contains("belongs to another user", ex.Message);
    }

    // The caller's own personal VM needs nothing beyond the team-scoped Vm permission: the elevated
    // check another user's Vm takes is not asked, which the spy pins.
    [Fact]
    public async Task CanAccessVm_ForTheCallersOwnPersonalVm_IsAllowedWithoutEscalation()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm(teamIds: [teamId], userId: Caller);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamVms, teamId));

        Assert.True(await Service.CanAccessVm(vm, Ct));

        Assert.Empty(_spy.CallsTo(nameof(IPlayerService.Can)));
    }

    // An instructor or view admin does reach another user's personal VM - that is what the elevated
    // permission is for, and how the VM console shows up in a view-wide list.
    [Fact]
    public async Task CanAccessVm_ForAnotherUsersPersonalVm_IsAllowedWithViewPermission()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm(teamIds: [teamId], userId: OtherUser);
        Grant(b => b.InView(teamId, viewId).Holding(AppTeamPermission.ViewTeamVms, teamId).Holding(AppViewPermission.ViewViewVms, viewId));

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
    public async Task GetByTeamId_is_forbidden_for_a_caller_holding_ViewTeamVms_only_on_another_team()
    {
        var teamId = Guid.NewGuid();
        await Seed(Vm(teamIds: [teamId]));
        Visibility(teamId, VisibilityFor(teamId));
        Grant(b => b.Holding(AppTeamPermission.ViewTeamVms, Guid.NewGuid()));

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
        // A View player.api does not have: its teams route answers 404.

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
    /// The visible teams' Vm access is asked for in one call, not one permission check per team.
    /// </summary>
    [Fact]
    public async Task GetByViewId_AsksForTheVisibleTeamsVmAccessInOneCall()
    {
        var viewId = Guid.NewGuid();
        var firstTeam = Guid.NewGuid();
        var secondTeam = Guid.NewGuid();

        var first = Vm(teamIds: [firstTeam], name: "first");
        var second = Vm(teamIds: [secondTeam], name: "second");
        await Seed(first, second);

        View(viewId, VisibilityFor(false, [firstTeam, secondTeam]), teams: [Team(firstTeam), Team(secondTeam)]);
        CanViewVms(true);

        var vms = await Service.GetByViewIdAsync(viewId, null, false, false, Ct);

        Assert.Equal(new HashSet<Guid> { first.Id, second.Id }, vms.Select(x => x.Id).ToHashSet());
        var batched = Assert.Single(_spy.CallsTo(nameof(IPlayerService.GetTeamIdsWithPermissionAsync)));
        Assert.Equal(viewId, batched[0]);
        Assert.True(((IEnumerable<Guid>)batched[1]).ToHashSet().SetEquals([firstTeam, secondTeam]));
        Assert.Equal(AppPermissions.VmReadTeam, batched[4]);
        Assert.Empty(_spy.CallsTo(nameof(IPlayerService.CanViewVms)));
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

    #region Every Vm and Map in a View

    // The way in for a caller who holds a Vm permission without being on the View's teams - an
    // administrator, or Steamfitter's service account running a task. Membership is not asked at all.
    [Fact]
    public async Task GetAllByViewId_is_forbidden_for_a_caller_holding_only_ViewTeamVms_on_a_team_of_the_view()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        Roster(viewId, teamId);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamVms, teamId).InView(teamId, viewId));

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetAllByViewIdAsync(viewId, Ct));
    }

    // The permission is asked about the View alone: a team-scoped grant does not reach every Vm in it.
    [Fact]
    public async Task GetAllByViewId_AsksForAViewLevelPermissionOnly()
    {
        var viewId = Guid.NewGuid();
        Roster(viewId, Guid.NewGuid());
        CanViewVms(true);

        await Service.GetAllByViewIdAsync(viewId, Ct);

        var asked = Assert.Single(_spy.CallsTo(nameof(IPlayerService.CanViewVms)));
        Assert.Empty((IEnumerable<Guid>)asked[0]);
        Assert.Equal([viewId], (IEnumerable<Guid>)asked[1]);
    }

    [Fact]
    public async Task GetAllByViewId_ForAnUnknownView_IsNull()
    {
        var viewId = Guid.NewGuid();
        _views.GetTeamsForView(viewId, true, Arg.Any<CancellationToken>()).Returns((List<Guid>)null);
        CanViewVms(true);

        Assert.Null(await Service.GetAllByViewIdAsync(viewId, Ct));
    }

    /// <summary>
    /// Everything on the View's teams, personal Vms included, for a caller on none of them - and nothing
    /// from teams outside the View.
    /// </summary>
    [Fact]
    public async Task GetAllByViewId_ReturnsEveryVmOnTheViewsTeams()
    {
        var viewId = Guid.NewGuid();
        var firstTeam = Guid.NewGuid();
        var secondTeam = Guid.NewGuid();

        var first = Vm(teamIds: [firstTeam], name: "first");
        var personal = Vm(teamIds: [secondTeam], name: "personal", userId: OtherUser);
        var elsewhere = Vm(teamIds: [Guid.NewGuid()], name: "elsewhere");
        await Seed(first, personal, elsewhere);

        Roster(viewId, firstTeam, secondTeam);
        CanViewVms(true);

        var vms = await Service.GetAllByViewIdAsync(viewId, Ct);

        Assert.Equal(new HashSet<Guid> { first.Id, personal.Id }, vms.Select(x => x.Id).ToHashSet());
    }

    // A Vm shared into another View appears once, carrying only this View's teams: the permission
    // checked says nothing about the other View.
    [Fact]
    public async Task GetAllByViewId_MasksTeamIdsFromOutsideTheView()
    {
        var viewId = Guid.NewGuid();
        var inView = Guid.NewGuid();
        var vm = Vm(teamIds: [inView, Guid.NewGuid()]);
        await Seed(vm);

        Roster(viewId, inView);
        CanViewVms(true);

        var vms = await Service.GetAllByViewIdAsync(viewId, Ct);

        Assert.Equal<Guid>([inView], vms.Single().TeamIds.ToArray());
    }

    [Fact]
    public async Task GetAllViewMaps_is_forbidden_for_a_caller_holding_only_ViewTeamMaps_on_a_team_of_the_view()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        Roster(viewId, teamId);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamMaps, teamId).InView(teamId, viewId));

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetAllViewMapsAsync(viewId, Ct));
    }

    [Fact]
    public async Task GetAllViewMaps_ForAnUnknownView_IsNull()
    {
        var viewId = Guid.NewGuid();
        _views.GetTeamsForView(viewId, true, Arg.Any<CancellationToken>()).Returns((List<Guid>)null);
        CanViewMaps(true);

        Assert.Null(await Service.GetAllViewMapsAsync(viewId, Ct));
    }

    // Teamless Maps included: the View-level Map permission this endpoint takes is what reading them takes.
    [Fact]
    public async Task GetAllViewMaps_ReturnsEveryMapInTheView()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var teamless = Map(teamIds: [], viewId: viewId);
        var teamScoped = Map(teamIds: [teamId], viewId: viewId);
        await Seed(teamless, teamScoped, Map(teamIds: [], viewId: Guid.NewGuid()));

        Roster(viewId, teamId);
        CanViewMaps(true);

        var maps = await Service.GetAllViewMapsAsync(viewId, Ct);

        Assert.Equal(new HashSet<Guid> { teamless.Id, teamScoped.Id }, maps.Select(x => x.Id).ToHashSet());
    }

    #endregion

    #region System-wide reads

    // GetAllAsync and GetAllMapsAsync have no team to scope to, so they are gated on a system
    // permission outright. These are the endpoints an administrative UI uses.
    [Fact]
    public async Task GetAll_is_forbidden_for_a_caller_holding_only_ViewViewVms()
    {
        Grant(b => b.Holding(AppViewPermission.ViewViewVms));

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetAllAsync(Ct));
    }

    [Fact]
    public async Task GetAll_is_allowed_for_a_caller_holding_exactly_ViewVms()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()]);
        await Seed(vm);
        Grant(b => b.Holding(AppSystemPermission.ViewVms));

        Assert.Contains(vm.Id, (await Service.GetAllAsync(Ct)).Select(x => x.Id));
    }

    [Fact]
    public async Task GetAllMaps_is_forbidden_for_a_caller_holding_only_ViewViewMaps()
    {
        Grant(b => b.Holding(AppViewPermission.ViewViewMaps));

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

    [Fact]
    public async Task GetMap_is_forbidden_for_a_caller_holding_only_ViewTeamVms()
    {
        var teamId = Guid.NewGuid();
        var map = Map(teamIds: [teamId]);
        await Seed(map);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamVms, teamId));

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetMapAsync(map.Id, Ct));
    }

    /// <summary>
    /// A Map assigned to no team is readable with a View- or system-level Map permission. It is asked
    /// about with the View and no teams, so a Map permission on some team in the View does not reach it.
    /// </summary>
    [Fact]
    public async Task GetMap_WithNoTeams_IsForbiddenWithoutAViewLevelMapPermission()
    {
        var map = Map(teamIds: []);
        await Seed(map);
        var teamId = Guid.NewGuid();
        Grant(b => b.InView(teamId, map.ViewId).Holding(AppTeamPermission.ViewTeamMaps, teamId));

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetMapAsync(map.Id, Ct));
    }

    // Viewing is enough: reading a teamless Map does not take the permission to manage it.
    [Fact]
    public async Task GetMap_WithNoTeams_IsAllowedByAViewLevelMapPermission()
    {
        var map = Map(teamIds: []);
        await Seed(map);
        Grant(b => b.Holding(AppViewPermission.ViewViewMaps, map.ViewId));

        Assert.NotNull(await Service.GetMapAsync(map.Id, Ct));
    }

    [Fact]
    public async Task GetTeamMap_is_forbidden_for_a_caller_holding_only_ViewTeamVms()
    {
        var teamId = Guid.NewGuid();
        Grant(b => b.Holding(AppTeamPermission.ViewTeamVms, teamId));

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetTeamMapAsync(teamId, Ct));
    }

    [Fact]
    public async Task DeleteMap_is_forbidden_for_a_caller_holding_only_ViewTeamMaps()
    {
        var teamId = Guid.NewGuid();
        var map = Map(teamIds: [teamId]);
        await Seed(map);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamMaps, teamId));

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.DeleteMapAsync(map.Id, Ct));
    }

    // Deleting a shared Map takes it from every team it is on, so it takes management of all of them,
    // the same as moving it off them with an update.
    [Fact]
    public async Task DeleteMap_ManagingOnlySomeOfItsTeams_IsForbidden()
    {
        var (managed, unmanaged) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map(teamIds: [managed, unmanaged]);
        await Seed(map);
        CanManageMapsOnly(managed);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.DeleteMapAsync(map.Id, Ct));
    }

    [Fact]
    public async Task DeleteMap_ManagingEveryOneOfItsTeams_DeletesIt()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map(teamIds: [first, second]);
        await Seed(map);
        CanManageMapsOnly(first, second);

        Assert.True(await Service.DeleteMapAsync(map.Id, Ct));
    }

    // A Map belonging to the View as a whole takes a View-level grant to delete.
    [Fact]
    public async Task DeleteMap_of_a_teamless_map_is_forbidden_for_a_caller_holding_only_ManageTeamMaps()
    {
        var map = Map(teamIds: []);
        await Seed(map);
        Grant(b => b.Holding(AppTeamPermission.ManageTeamMaps));

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
        // A View player.api does not have: its teams route answers 404.

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

        var maps = await Service.GetViewMapsAsync(viewId, Ct);

        Assert.Equal<Guid>([allowed.Id], maps.Select(x => x.Id).ToArray());
    }

    /// <summary>
    /// The listing counterpart of <see cref="GetMap_WithNoTeams_IsForbiddenWithoutAViewLevelMapPermission"/>:
    /// the View's teamless Maps are listed for a caller with a View- or system-level Map permission, and
    /// for no one else.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetViewMaps_IncludesTheTeamlessMapsOnlyWithAViewLevelMapPermission(bool viewLevel)
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var teamless = Map(teamIds: [], viewId: viewId);
        var teamScoped = Map(teamIds: [teamId], viewId: viewId);
        await Seed(teamless, teamScoped);

        View(viewId, VisibilityFor(teamId), teams: [Team(teamId)]);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamMaps, teamId));

        if (viewLevel)
            Grant(b => b.Holding(AppViewPermission.ViewViewMaps, viewId));

        var maps = await Service.GetViewMapsAsync(viewId, Ct);

        var expected = viewLevel ? new HashSet<Guid> { teamless.Id, teamScoped.Id } : [teamScoped.Id];
        Assert.Equal(expected, maps.Select(x => x.Id).ToHashSet());
    }

    /// <summary>
    /// Every Map's teams are asked about in one call, not one permission check per Map.
    /// </summary>
    [Fact]
    public async Task GetViewMaps_AsksForEveryMapsTeamsInOneCall()
    {
        var viewId = Guid.NewGuid();
        var firstTeam = Guid.NewGuid();
        var secondTeam = Guid.NewGuid();

        await Seed(Map(teamIds: [firstTeam], viewId: viewId), Map(teamIds: [secondTeam], viewId: viewId));

        View(viewId, VisibilityFor(firstTeam), teams: [Team(firstTeam), Team(secondTeam)]);
        CanViewMaps(true);

        Assert.Equal(2, (await Service.GetViewMapsAsync(viewId, Ct)).Length);
        var batched = Assert.Single(_spy.CallsTo(nameof(IPlayerService.GetTeamIdsWithPermissionAsync)));
        Assert.Equal(viewId, batched[0]);
        Assert.True(((IEnumerable<Guid>)batched[1]).ToHashSet().SetEquals([firstTeam, secondTeam]));
        Assert.Equal(AppPermissions.MapReadTeam, batched[4]);
        Assert.Empty(_spy.CallsTo(nameof(IPlayerService.CanViewMaps)));
    }

    // The View-level permission is a property of the View, not of any one teamless Map, so it is asked once.
    [Fact]
    public async Task GetViewMaps_AsksForTheViewLevelMapPermissionOnce()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        await Seed(Map(teamIds: [], viewId: viewId), Map(teamIds: [], viewId: viewId), Map(teamIds: [], viewId: viewId));

        View(viewId, VisibilityFor(teamId), teams: [Team(teamId)]);
        Grant(b => b.Holding(AppViewPermission.ViewViewMaps, viewId));

        Assert.Equal(3, (await Service.GetViewMapsAsync(viewId, Ct)).Length);
        Assert.Single(_spy.CallsTo(nameof(IPlayerService.CanViewMaps)));
    }

    // A map can only be assigned to teams the caller manages, and the view must exist. Both failures are
    // flattened into ForbiddenException by validateViewAndTeams, including the not-found case.
    [Fact]
    public async Task CreateMap_ForAViewThatDoesNotExist_IsForbidden()
    {
        // A View player.api does not have: its view route answers 404.

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [] }, Guid.NewGuid(), Ct));

        Assert.Contains("View does not exist", ex.Message);
    }

    [Fact]
    public async Task CreateMap_ForATeamInAnotherView_IsForbidden()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        _player.Directory.AddView(viewId);
        _player.Directory.PlaceTeam(teamId, Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [teamId] }, viewId, Ct));

        Assert.Contains("is not a member of the specified view", ex.Message);
    }

    [Fact]
    public async Task CreateMap_is_forbidden_for_a_caller_holding_only_ViewTeamMaps()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        ViewWithTeams(viewId, teamId);
        Grant(b => b.Holding(AppTeamPermission.ViewTeamMaps, teamId));

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [teamId] }, viewId, Ct));
    }

    /// <summary>
    /// A Map with no teams belongs to the View as a whole, so creating one takes the View-level Map
    /// permission, asked for with the View id and no teams.
    /// </summary>
    [Fact]
    public async Task CreateMap_of_a_teamless_map_is_forbidden_for_a_caller_holding_only_ManageTeamMaps()
    {
        var viewId = Guid.NewGuid();
        ViewWithTeams(viewId);
        Grant(b => b.Holding(AppTeamPermission.ManageTeamMaps));

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [] }, viewId, Ct));
    }

    [Fact]
    public async Task CreateMap_WithNoTeams_IsAllowedByAViewLevelMapPermission()
    {
        var viewId = Guid.NewGuid();
        ViewWithTeams(viewId);
        Grant(b => b.Holding(AppViewPermission.ManageViewMaps, viewId));

        var map = await Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [] }, viewId, Ct);

        Assert.Equal(viewId, map.ViewId);
    }

    /// <summary>
    /// Managing the teams a Map is moving to is not enough: the caller must also manage the teams it is
    /// moving off, or someone who manages team B's Maps could take team A's.
    /// </summary>
    [Fact]
    public async Task UpdateMap_MovingAMapOffATeamTheCallerCannotManage_IsForbidden()
    {
        var viewId = Guid.NewGuid();
        var (fromTeam, toTeam) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map(teamIds: [fromTeam], viewId: viewId);
        await Seed(map);
        ViewWithTeams(viewId, fromTeam, toTeam);
        CanManageMapsOnly(toTeam);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.UpdateMapAsync(UpdateForm(toTeam), map.Id, Ct));
    }

    // A Map with no teams stands for the View as a whole, so moving it onto a team takes the View-level
    // grant, whatever the caller holds on the team.
    [Fact]
    public async Task UpdateMap_ForATeamlessMap_IsForbiddenWithoutViewLevelMapManagement()
    {
        var viewId = Guid.NewGuid();
        var toTeam = Guid.NewGuid();
        var map = Map(teamIds: [], viewId: viewId);
        await Seed(map);
        ViewWithTeams(viewId, toTeam);
        CanManageMapsOnly(toTeam);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.UpdateMapAsync(UpdateForm(toTeam), map.Id, Ct));
    }

    [Fact]
    public async Task UpdateMap_ManagingTheOldAndNewTeams_MovesTheMap()
    {
        var viewId = Guid.NewGuid();
        var (fromTeam, toTeam) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map(teamIds: [fromTeam], viewId: viewId);
        await Seed(map);
        ViewWithTeams(viewId, fromTeam, toTeam);
        CanManageMapsOnly(fromTeam, toTeam);

        var updated = await Service.UpdateMapAsync(UpdateForm(toTeam), map.Id, Ct);

        Assert.Equal<Guid>([toTeam], updated.TeamIds);
    }

    #endregion

    #region Mutating a Vm

    // Every way of changing a VM or its team membership is gated on manage rights over the teams
    // involved; controlling the team's Vms is the near miss.

    [Fact]
    public async Task Update_is_forbidden_for_a_caller_holding_only_ControlTeamVms()
    {
        var vm = await SeedVmOnOneTeam(AppTeamPermission.ControlTeamVms);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.UpdateAsync(vm.Id, new VmUpdateForm { Name = "renamed" }, Ct));
    }

    [Fact]
    public async Task Delete_is_forbidden_for_a_caller_holding_only_ControlTeamVms()
    {
        var vm = await SeedVmOnOneTeam(AppTeamPermission.ControlTeamVms);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.DeleteAsync(vm.Id, Ct));
    }

    [Fact]
    public async Task AddToTeam_is_forbidden_for_a_caller_holding_only_ControlTeamVms()
    {
        var vm = await SeedVmOnOneTeam(AppTeamPermission.ControlTeamVms);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.AddToTeamAsync(vm.Id, vm.VmTeams.Single().TeamId, Ct));
    }

    [Fact]
    public async Task RemoveFromTeam_is_forbidden_for_a_caller_holding_only_ControlTeamVms()
    {
        var vm = await SeedVmOnOneTeam(AppTeamPermission.ControlTeamVms);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.RemoveFromTeamAsync(vm.Id, vm.VmTeams.Single().TeamId, Ct));
    }

    [Fact]
    public async Task Update_is_allowed_for_a_caller_holding_exactly_ManageViews()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()]);
        await Seed(vm);
        Grant(b => b.Holding(AppSystemPermission.ManageViews));

        await Service.UpdateAsync(vm.Id, new VmUpdateForm { Name = "renamed" }, Ct);

        await using var context = NewContext();
        Assert.Equal("renamed", context.Vms.Single(x => x.Id == vm.Id).Name);
    }

    [Fact]
    public async Task Delete_is_allowed_for_a_caller_holding_exactly_ManageTeam_on_the_Vms_team()
    {
        var vm = await SeedVmOnOneTeam(AppTeamPermission.ManageTeam);

        Assert.True(await Service.DeleteAsync(vm.Id, Ct));

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == vm.Id));
    }

    [Fact]
    public async Task AddToTeam_is_allowed_for_a_caller_holding_exactly_ManageTeam_on_the_target_team()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()]);
        await Seed(vm);
        var target = Guid.NewGuid();
        Grant(b => b.Holding(AppTeamPermission.ManageTeam, target));

        Assert.True(await Service.AddToTeamAsync(vm.Id, target, Ct));

        await using var context = NewContext();
        Assert.Single(context.VmTeams.Where(x => x.VmId == vm.Id && x.TeamId == target));
    }

    [Fact]
    public async Task Create_is_allowed_for_a_caller_holding_exactly_ManageTeam_on_every_team()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        Grant(b => b.Holding(AppTeamPermission.ManageTeam, first).Holding(AppTeamPermission.ManageTeam, second));

        var form = new VmCreateForm { Id = Guid.NewGuid(), Name = "new", TeamIds = [first, second] };

        Assert.Equal(form.Id, (await Service.CreateAsync(form, Ct)).Id);
    }

    [Fact]
    public async Task Create_is_forbidden_for_a_caller_holding_ManageTeam_on_only_one_of_its_teams()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        Grant(b => b.Holding(AppTeamPermission.ManageTeam, first));

        var form = new VmCreateForm { Id = Guid.NewGuid(), Name = "new", TeamIds = [first, second] };

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.CreateAsync(form, Ct));
    }

    [Fact]
    public async Task Create_is_forbidden_for_a_caller_holding_only_ControlTeamVms()
    {
        var teamId = Guid.NewGuid();
        Grant(b => b.Holding(AppTeamPermission.ControlTeamVms, teamId));

        var form = new VmCreateForm { Id = Guid.NewGuid(), Name = "new", TeamIds = [teamId] };

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
    public async Task RemoveFromTeam_is_allowed_for_a_caller_holding_exactly_ManageTeam_on_that_team_when_another_remains()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var vm = Vm(teamIds: [first, second]);
        await Seed(vm);
        Grant(b => b.Holding(AppTeamPermission.ManageTeam, first));

        Assert.True(await Service.RemoveFromTeamAsync(vm.Id, first, Ct));

        await using var context = NewContext();
        Assert.Equal<Guid>([second], context.VmTeams.Where(x => x.VmId == vm.Id).Select(x => x.TeamId).ToArray());
    }

    #endregion

    #region Helpers

    private static ClaimsPrincipal Principal(Guid userId) =>
        new(new ClaimsIdentity([new Claim("sub", userId.ToString())], "test"));

    /// <summary>A Vm permission on every team: the system <c>ViewVms</c>. False grants nothing.</summary>
    private void CanViewVms(bool allowed)
    {
        if (allowed)
            Grant(b => b.Holding(AppSystemPermission.ViewVms));
    }

    /// <summary>A Map permission on every team and View: the system <c>ViewMaps</c>. False grants nothing.</summary>
    private void CanViewMaps(bool allowed)
    {
        if (allowed)
            Grant(b => b.Holding(AppSystemPermission.ViewMaps));
    }

    /// <summary>What GetAllByViewIdAsync and GetAllViewMapsAsync read the View's teams from.</summary>
    private void Roster(Guid viewId, params Guid[] teamIds) =>
        _views.GetTeamsForView(viewId, true, Arg.Any<CancellationToken>()).Returns(teamIds.ToList());

    /// <summary>Map management everywhere: the system <c>ManageMaps</c>. False grants nothing.</summary>
    private void CanManageMaps(bool allowed)
    {
        if (allowed)
            Grant(b => b.Holding(AppSystemPermission.ManageMaps));
    }

    /// <summary>
    /// Map management on exactly these teams and nothing View-wide, so a teamless Map is not manageable.
    /// </summary>
    private void CanManageMapsOnly(params Guid[] teamIds)
    {
        foreach (var teamId in teamIds)
            Grant(b => b.Holding(AppTeamPermission.ManageTeamMaps, teamId));
    }

    /// <summary>What validateViewAndTeams checks before any permission: the View and its teams exist.</summary>
    private void ViewWithTeams(Guid viewId, params Guid[] teamIds)
    {
        _player.Directory.AddView(viewId);
        foreach (var teamId in teamIds)
            _player.Directory.PlaceTeam(teamId, viewId);
    }

    private static VmMapUpdateForm UpdateForm(params Guid[] teamIds) =>
        new() { Name = "map", Coordinates = [], TeamIds = [.. teamIds] };

    /// <summary>Vm access on exactly these teams: <c>ViewTeamVms</c> on each, and nothing View-wide.</summary>
    private void CanViewVmsOnly(params Guid[] teamIds)
    {
        foreach (var teamId in teamIds)
            Grant(b => b.Holding(AppTeamPermission.ViewTeamVms, teamId));
    }

    /// <summary>The Map counterpart of <see cref="CanViewVmsOnly"/>: <c>ViewTeamMaps</c> on each.</summary>
    private void CanViewMapsOnly(params Guid[] teamIds)
    {
        foreach (var teamId in teamIds)
            Grant(b => b.Holding(AppTeamPermission.ViewTeamMaps, teamId));
    }

    /// <summary>A saved Vm on one team, and a caller holding <paramref name="permission"/> on that team only.</summary>
    private async Task<VmEntity> SeedVmOnOneTeam(AppTeamPermission permission)
    {
        var teamId = Guid.NewGuid();
        var vm = Vm(teamIds: [teamId]);
        await Seed(vm);
        Grant(b => b.Holding(permission, teamId));

        return vm;
    }

    private void Grant(Func<TestActorBuilder, TestActorBuilder> steps) => _player.Grant(steps);

    /// <summary>Team management everywhere: the system <c>ManageViews</c>. False grants nothing.</summary>
    private void CanManageTeams(bool allowed)
    {
        if (allowed)
            Grant(b => b.Holding(AppSystemPermission.ManageViews));
    }

    /// <summary>
    /// The caller's visibility for the team's View, as player.api's data produces it: the team placed in a
    /// View (its own, unless placed already), and the caller's primary team there (see <see cref="Visible"/>).
    /// </summary>
    private void Visibility(Guid teamId, VisibilityContext context)
    {
        var viewId = _player.Directory.ViewOf(teamId) ?? Guid.NewGuid();
        _player.Directory.PlaceTeam(teamId, viewId);
        Visible(viewId, context);
    }

    /// <summary>The View's teams placed in it, and the caller's visibility there.</summary>
    private void View(Guid viewId, VisibilityContext visibility, params Guid[] teamIds) =>
        View(viewId, visibility, teamIds.Select(x => Team(x)).ToArray());

    private void View(Guid viewId, VisibilityContext visibility, Player.Api.Client.Team[] teams)
    {
        _player.Directory.AddView(viewId);

        foreach (var team in teams)
            _player.Directory.PlaceTeam(team.Id, viewId);

        Visible(viewId, visibility);
    }

    /// <summary>The player.api data the caller's visibility in the View comes from (see <see cref="PlayerHarness.Sees(Guid, VisibilityContext)"/>).</summary>
    private void Visible(Guid viewId, VisibilityContext context) => _player.Sees(viewId, context);

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

    /// <summary>A Map, with its View and teams placed in player.api: a Map's teams are teams of its View.</summary>
    private VmMapEntity Map(Guid[] teamIds, Guid? viewId = null)
    {
        var map = new VmMapEntity
        {
            Id = Guid.NewGuid(),
            Name = "map",
            ViewId = viewId ?? Guid.NewGuid(),
            TeamIds = [.. teamIds]
        };

        _player.Directory.AddView(map.ViewId);
        foreach (var teamId in teamIds)
            _player.Directory.PlaceTeam(teamId, map.ViewId);

        return map;
    }

    #endregion
}
