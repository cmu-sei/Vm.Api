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

    [Fact]
    public async Task CanAccessVm_WithoutViewAccessToItsTeams_IsForbidden()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()]);
        CanViewTeams(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.CanAccessVm(vm, Ct));
    }

    [Fact]
    public async Task CanAccessVm_ForASharedVmInAVisibleTeam_IsAllowed()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()]);
        CanViewTeams(true);

        Assert.True(await Service.CanAccessVm(vm, Ct));
    }

    /// <summary>
    /// A personal VM belongs to one user. Team-level view access is not enough to reach someone else's,
    /// which is what keeps one student out of another's workstation.
    /// </summary>
    [Fact]
    public async Task CanAccessVm_ForAnotherUsersPersonalVm_IsForbidden()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()], userId: OtherUser);
        CanViewTeams(true);
        Can(false);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Service.CanAccessVm(vm, Ct));

        Assert.Contains("belongs to another user", ex.Message);
    }

    // The caller's own personal VM needs nothing beyond team view access - no elevated permission is
    // consulted at all, which is what the DidNotReceive pins.
    [Fact]
    public async Task CanAccessVm_ForTheCallersOwnPersonalVm_IsAllowedWithoutEscalation()
    {
        var vm = Vm(teamIds: [Guid.NewGuid()], userId: Caller);
        CanViewTeams(true);

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
        CanViewTeams(true);
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

        var vms = await Service.GetByViewIdAsync(viewId, null, false, onlyMine: true, Ct);

        Assert.Equal(onPrimary.Id, vms.First().Id);
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
        Can(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetAllAsync(Ct));
    }

    [Fact]
    public async Task GetAllMaps_WithoutASystemPermission_IsForbidden()
    {
        Can(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetAllMapsAsync(Ct));
    }

    [Fact]
    public async Task GetAll_WithASystemPermission_ReturnsEveryVm()
    {
        await Seed(Vm(teamIds: [Guid.NewGuid()]), Vm(teamIds: [Guid.NewGuid()]));
        Can(true);

        Assert.Equal(2, (await Service.GetAllAsync(Ct)).Length);
    }

    #endregion

    #region Maps

    [Fact]
    public async Task GetMap_WithoutAccessToItsTeams_IsForbidden()
    {
        var map = Map(teamIds: [Guid.NewGuid()]);
        await Seed(map);
        CanViewTeams(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetMapAsync(map.Id, Ct));
    }

    /// <summary>
    /// A map assigned to no team is readable by anyone authenticated: the permission check is guarded on
    /// <c>TeamIds.Count > 0</c>. That is deliberate - an unassigned map has nothing to protect - but it
    /// means creating a map with no teams makes it world-readable, so the check is pinned rather than
    /// left to be rediscovered.
    /// </summary>
    [Fact]
    public async Task GetMap_WithNoTeams_SkipsThePermissionCheck()
    {
        var map = Map(teamIds: []);
        await Seed(map);
        CanViewTeams(false);

        Assert.NotNull(await Service.GetMapAsync(map.Id, Ct));
        await _player.DidNotReceive().CanViewTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTeamMap_ForAnInvisibleTeam_IsForbidden()
    {
        _player.IsTeamVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetTeamMapAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task DeleteMap_WithoutManageOnItsTeams_IsForbidden()
    {
        var map = Map(teamIds: [Guid.NewGuid()]);
        await Seed(map);
        CanManageTeams(false);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.DeleteMapAsync(map.Id, Ct));
    }

    /// <summary>
    /// null, so the controller answers 404. An unknown View must not read as a View with no maps, which
    /// is why GetViewMapsAsync probes the teams endpoint even though it filters on visibility.TeamIds.
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
    public async Task GetViewMaps_ReturnsOnlyMapsWithAVisibleTeam()
    {
        var viewId = Guid.NewGuid();
        var visibleTeam = Guid.NewGuid();
        var hiddenTeam = Guid.NewGuid();

        var visible = Map(teamIds: [visibleTeam], viewId: viewId);
        var hidden = Map(teamIds: [hiddenTeam], viewId: viewId);
        await Seed(visible, hidden);

        View(viewId, VisibilityFor(visibleTeam), teams: [Team(visibleTeam), Team(hiddenTeam)]);

        var maps = await Service.GetViewMapsAsync(viewId, Ct);

        Assert.Equal<Guid>([visible.Id], maps.Select(x => x.Id).ToArray());
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
    public async Task CreateMap_WithoutManageOnTheTeams_IsForbidden()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        _player.GetViewByIdAsync(viewId, Arg.Any<CancellationToken>()).Returns(new Player.Api.Client.View());
        _player.GetTeamById(teamId).Returns(new Player.Api.Client.Team { Id = teamId, ViewId = viewId });
        CanManageTeams(false);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Service.CreateMapAsync(new VmMapCreateForm { Name = "m", TeamIds = [teamId] }, viewId, Ct));
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

    private void CanViewTeams(bool allowed) =>
        _player.CanViewTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(allowed);

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
