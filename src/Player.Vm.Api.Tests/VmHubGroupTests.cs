// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmDto = Player.Vm.Api.Features.Vms.Vm;
using VmUserEntity = Player.Vm.Api.Domain.Models.VmUser;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The subscribe half of VmHub: the six join and leave methods, and the group names they compute.
///
/// This is the hub's authorization surface, and unlike every controller it decides for itself rather
/// than delegating. There is no attribute and no handler behind these methods - the endpoint requires a
/// token and nothing more - so what a caller may subscribe to is whatever these methods do with the
/// visibility context player.api hands back. Three of the six ask for a group id list and add whatever
/// comes back, one throws a HubException, and two filter view by view.
///
/// The names matter as much as the filtering, because they are the contract with whatever broadcasts:
/// JoinView's bare group guid is what VmCreatedSignalRHandler and its siblings send to, and the
/// ActiveConsoles and CurrentVirtualMachineUsers names are what SetActiveVirtualMachine sends to. A name
/// changed on one side only breaks every subscriber silently, which is why they are spelled out here
/// rather than taken from the hub's own private helpers.
/// </summary>
public class VmHubGroupTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>
    /// VmHub's own <c>UserGroupPrefix</c>, which is private. Restated rather than reflected on: the
    /// point of these tests is the literal name a client has to agree with, and reading it back out of
    /// the hub would make every assertion here pass whatever it was changed to.
    /// </summary>
    private const string UserPrefix = "ActiveConsoles";

    /// <summary>
    /// The per-Vm presence channel prefix. This one the hub takes from
    /// <c>VmHubMethods.CurrentVirtualMachineUsers</c>, so the constant is shared with the client method
    /// name - a group and a method that happen to be spelled the same thing.
    /// </summary>
    private const string VmUsersPrefix = "CurrentVirtualMachineUsers";

    private readonly IPlayerService _player = Substitute.For<IPlayerService>();
    private readonly IActiveVirtualMachineService _active = Substitute.For<IActiveVirtualMachineService>();
    private readonly IVmService _vms = Substitute.For<IVmService>();
    private readonly IViewService _views = Substitute.For<IViewService>();
    private readonly IVmUsageLoggingService _usageLog = Substitute.For<IVmUsageLoggingService>();
    private readonly HubHarness _harness = new(Guid.NewGuid());

    private VmHub Hub => _harness.Attach(
        new VmHub(_active, _usageLog, _views, _player, _vms, Db));

    #region JoinView and LeaveView

    /// <summary>
    /// The plain view feed. player.api decides what the group ids are - the view id for a caller who can
    /// see every team, otherwise each visible team id - and the hub adds every one of them without
    /// filtering or renaming.
    /// </summary>
    [Fact]
    public async Task JoinView_AddsEveryGroupPlayerApiReports()
    {
        var viewId = Guid.NewGuid();
        Guid[] groupIds = [Guid.NewGuid(), Guid.NewGuid()];
        GroupIds(viewId, groupIds);

        await Hub.JoinView(viewId);

        Assert.Equal<string>(groupIds.Select(x => x.ToString()), _harness.Added);
    }

    /// <summary>
    /// The bare guid, with none of the prefixes the other joins use. This is the name
    /// <c>VmBaseSignalRHandler.HandleCreateOrUpdate</c> sends VmCreated, VmUpdated and VmDeleted to, so
    /// a prefix added here would leave every Vm list in every client permanently stale.
    /// </summary>
    [Fact]
    public async Task JoinView_NamesTheGroupWithNoPrefix()
    {
        var groupId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        GroupIds(viewId, groupId);

        await Hub.JoinView(viewId);

        Assert.Equal<string>([groupId.ToString()], _harness.Added);
    }

    [Fact]
    public async Task JoinView_AddsTheCallersOwnConnection()
    {
        var viewId = Guid.NewGuid();
        GroupIds(viewId, Guid.NewGuid());

        await Hub.JoinView(viewId);

        Assert.Equal(_harness.ConnectionId, Assert.Single(_harness.AddedChanges).ConnectionId);
    }

    /// <summary>
    /// A caller with no visibility in the view gets no groups and no error. That is the refusal: an
    /// empty group id list from player.api is what an unknown view or a caller who is not on any of its
    /// teams looks like, and the hub simply subscribes them to nothing.
    /// </summary>
    [Fact]
    public async Task JoinView_ForAViewTheCallerCannotSee_AddsNothing()
    {
        var viewId = Guid.NewGuid();
        GroupIds(viewId);

        await Hub.JoinView(viewId);

        Assert.Empty(_harness.Added);
    }

    [Fact]
    public async Task LeaveView_RemovesExactlyWhatJoinViewAdded()
    {
        var viewId = Guid.NewGuid();
        GroupIds(viewId, Guid.NewGuid(), Guid.NewGuid());
        var hub = Hub;

        await hub.JoinView(viewId);
        await hub.LeaveView(viewId);

        Assert.Equal(_harness.Added, _harness.Removed);
    }

    /// <summary>
    /// Leaving asks player.api again rather than remembering what it joined, so a caller whose
    /// visibility has narrowed since they joined leaves only the groups they can still see - and stays
    /// subscribed to the rest for the life of the connection.
    /// </summary>
    /// <remarks>
    /// Characterized, not fixed. It is bounded by the connection: the group memberships go when the
    /// connection does. Fixing it means tracking the joined names per connection in <c>Context.Items</c>
    /// and leaving those, and this test is what would say so.
    /// </remarks>
    [Fact]
    public async Task LeaveView_WhenVisibilityNarrowed_LeavesOnlyWhatIsStillVisible()
    {
        var viewId = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var lost = Guid.NewGuid();
        GroupIds(viewId, kept, lost);
        var hub = Hub;

        await hub.JoinView(viewId);
        GroupIds(viewId, kept);
        await hub.LeaveView(viewId);

        Assert.Equal<string>([kept.ToString()], _harness.Removed);
    }

    #endregion

    #region JoinViewUsers and LeaveViewUsers

    [Fact]
    public async Task JoinViewUsers_AddsThePrefixedGroupForEveryGroupId()
    {
        var viewId = Guid.NewGuid();
        Guid[] groupIds = [Guid.NewGuid(), Guid.NewGuid()];
        GroupIds(viewId, groupIds);
        Teams(viewId);

        await Hub.JoinViewUsers(viewId);

        Assert.Equal<string>(groupIds.Select(x => $"{UserPrefix}-{x}"), _harness.Added);
    }

    [Fact]
    public async Task JoinViewUsers_ForAViewTheCallerCannotSee_AddsNothingAndReturnsNothing()
    {
        var viewId = Guid.NewGuid();
        GroupIds(viewId);

        var result = await Hub.JoinViewUsers(viewId);

        Assert.Empty(result);
        Assert.Empty(_harness.Added);
        await _player.DidNotReceive().GetTeamsByViewIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// player.api answering with no teams at all leaves the caller subscribed to the groups the hub
    /// already joined, with nothing returned to populate the list from. Not a security question - the
    /// group ids came from the visibility context - but it is why an empty user list is not proof that
    /// nothing was joined.
    /// </summary>
    [Fact]
    public async Task JoinViewUsers_WhenPlayerApiReportsNoTeams_StillLeavesTheCallerSubscribed()
    {
        var viewId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        GroupIds(viewId, groupId);
        _player.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>()).Returns((IEnumerable<Team>)null);

        var result = await Hub.JoinViewUsers(viewId);

        Assert.Empty(result);
        Assert.Equal<string>([$"{UserPrefix}-{groupId}"], _harness.Added);
    }

    [Fact]
    public async Task JoinViewUsers_ReturnsATeamPerTeamWithItsUsers()
    {
        var viewId = Guid.NewGuid();
        var teamA = new Team { Id = Guid.NewGuid(), Name = "Red" };
        var teamB = new Team { Id = Guid.NewGuid(), Name = "Blue" };
        var alice = new User { Id = Guid.NewGuid(), Name = "alice" };
        var bob = new User { Id = Guid.NewGuid(), Name = "bob" };

        GroupIds(viewId, viewId);
        Teams(viewId, teamA, teamB);
        UsersIn(teamA.Id, alice);
        UsersIn(teamB.Id, bob);

        var result = (await Hub.JoinViewUsers(viewId)).ToArray();

        Assert.Equal(2, result.Length);
        var red = result.Single(x => x.Id == teamA.Id);
        Assert.Equal("Red", red.Name);
        Assert.Equal<Guid>([alice.Id], red.Users.Select(x => x.UserId));
        Assert.Equal<string>(["alice"], red.Users.Select(x => x.Username));
        Assert.Equal<Guid>([bob.Id], result.Single(x => x.Id == teamB.Id).Users.Select(x => x.UserId));
    }

    /// <summary>
    /// A user's active Vm is reported only against the team they were on when they opened it. The same
    /// user appearing on two teams of the view shows as active on one and idle on the other, which is
    /// what keeps a view-admin's team list from claiming a console is open in a team it is not.
    /// </summary>
    [Fact]
    public async Task JoinViewUsers_ReportsAnActiveVmOnlyAgainstTheTeamItWasOpenedFrom()
    {
        var viewId = Guid.NewGuid();
        var teamA = new Team { Id = Guid.NewGuid(), Name = "Red" };
        var teamB = new Team { Id = Guid.NewGuid(), Name = "Blue" };
        var alice = new User { Id = Guid.NewGuid(), Name = "alice" };
        var vmId = Guid.NewGuid();

        GroupIds(viewId, viewId);
        Teams(viewId, teamA, teamB);
        UsersIn(teamA.Id, alice);
        UsersIn(teamB.Id, alice);
        _active.GetActiveVirtualMachineForUser(alice.Id)
            .Returns(new ActiveVirtualMachine(vmId, "other-connection", [teamA.Id], alice.Name));

        var result = (await Hub.JoinViewUsers(viewId)).ToArray();

        Assert.Equal(vmId, result.Single(x => x.Id == teamA.Id).Users.Single().ActiveVmId);
        Assert.Null(result.Single(x => x.Id == teamB.Id).Users.Single().ActiveVmId);
    }

    [Fact]
    public async Task JoinViewUsers_ReportsTheLastSeenVmFromTheDatabase()
    {
        var viewId = Guid.NewGuid();
        var team = new Team { Id = Guid.NewGuid(), Name = "Red" };
        var alice = new User { Id = Guid.NewGuid(), Name = "alice" };
        var vm = VmApiFactory.VsphereVm(team.Id);
        var lastSeen = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        await Seed(vm, new VmUserEntity(alice.Id, vm.Id, team.Id, lastSeen));

        GroupIds(viewId, viewId);
        Teams(viewId, team);
        UsersIn(team.Id, alice);

        var result = await Hub.JoinViewUsers(viewId);

        var user = result.Single().Users.Single();
        Assert.Equal(vm.Id, user.LastVmId);
        Assert.Equal(lastSeen, user.LastSeen);
        Assert.Null(user.ActiveVmId);
    }

    /// <summary>
    /// The row is matched on user <em>and</em> team. A user who has been seen on another team of the same
    /// view has a row that the query loads - it filters on the whole team id set - and it must not be
    /// reported here, or a view-admin's list would show a Vm the user opened somewhere else.
    /// </summary>
    [Fact]
    public async Task JoinViewUsers_DoesNotReportARowBelongingToAnotherTeam()
    {
        var viewId = Guid.NewGuid();
        var teamA = new Team { Id = Guid.NewGuid(), Name = "Red" };
        var teamB = new Team { Id = Guid.NewGuid(), Name = "Blue" };
        var alice = new User { Id = Guid.NewGuid(), Name = "alice" };
        var vm = VmApiFactory.VsphereVm(teamB.Id);
        await Seed(vm, new VmUserEntity(alice.Id, vm.Id, teamB.Id, DateTimeOffset.UtcNow));

        GroupIds(viewId, viewId);
        Teams(viewId, teamA, teamB);
        UsersIn(teamA.Id, alice);
        UsersIn(teamB.Id);

        var result = await Hub.JoinViewUsers(viewId);

        Assert.Null(result.Single(x => x.Id == teamA.Id).Users.Single().LastVmId);
    }

    [Fact]
    public async Task JoinViewUsers_ForAUserWithNoRow_ReportsNoLastSeen()
    {
        var viewId = Guid.NewGuid();
        var team = new Team { Id = Guid.NewGuid(), Name = "Red" };
        var alice = new User { Id = Guid.NewGuid(), Name = "alice" };

        GroupIds(viewId, viewId);
        Teams(viewId, team);
        UsersIn(team.Id, alice);

        var user = (await Hub.JoinViewUsers(viewId)).Single().Users.Single();

        Assert.Null(user.LastVmId);
        Assert.Null(user.LastSeen);
    }

    [Fact]
    public async Task LeaveViewUsers_RemovesExactlyWhatJoinViewUsersAdded()
    {
        var viewId = Guid.NewGuid();
        GroupIds(viewId, Guid.NewGuid(), Guid.NewGuid());
        Teams(viewId);
        var hub = Hub;

        await hub.JoinViewUsers(viewId);
        await hub.LeaveViewUsers(viewId);

        Assert.Equal(_harness.Added, _harness.Removed);
    }

    #endregion

    #region JoinUser and LeaveUser

    /// <summary>
    /// The one refusal in the hub, and the only place any of it throws. Following a single user is the
    /// narrowest subscription here and the only one whose target the caller names directly, so this is
    /// the check that stops a caller following a user on a team they cannot see.
    /// </summary>
    [Fact]
    public async Task JoinUser_ForATeamTheCallerCannotSee_IsRefused()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        Visibility(viewId, primaryTeamId: Guid.NewGuid(), canViewAllTeams: false, Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<HubException>(
            () => Hub.JoinUser(Guid.NewGuid(), viewId, teamId));

        Assert.Equal("You do not have access to this team", ex.Message);
    }

    /// <summary>
    /// The refusal comes first: nothing is joined, and nothing about the user being followed is looked
    /// up. A check that ran after the group add would leave a refused caller subscribed.
    /// </summary>
    [Fact]
    public async Task JoinUser_WhenRefused_JoinsNothingAndLooksNothingUp()
    {
        var viewId = Guid.NewGuid();
        Visibility(viewId, primaryTeamId: Guid.NewGuid(), canViewAllTeams: false, Guid.NewGuid());

        await Assert.ThrowsAsync<HubException>(() => Hub.JoinUser(Guid.NewGuid(), viewId, Guid.NewGuid()));

        Assert.Empty(_harness.Added);
        _active.DidNotReceive().GetActiveVirtualMachineForUser(Arg.Any<Guid>());
        await _player.DidNotReceive().GetUserById(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An empty visibility context refuses too, which is what an unknown view or a caller on none of its
    /// teams looks like - <c>VisibilityContext.Empty</c> has no team ids, so no team id can be in it.
    /// </summary>
    [Fact]
    public async Task JoinUser_ForAViewTheCallerCannotSee_IsRefused()
    {
        var viewId = Guid.NewGuid();
        _player.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(VisibilityContext.Empty);

        await Assert.ThrowsAsync<HubException>(() => Hub.JoinUser(Guid.NewGuid(), viewId, Guid.NewGuid()));
    }

    /// <summary>
    /// An ordinary team member's subscription is keyed on the team, so it carries only what
    /// SetActiveVirtualMachine sends to that team's group.
    /// </summary>
    [Fact]
    public async Task JoinUser_ForATeamMember_ScopesTheGroupToTheTeam()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        Visibility(viewId, primaryTeamId: teamId, canViewAllTeams: false, teamId);
        _player.GetUserById(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId, Name = "alice" });

        await Hub.JoinUser(userId, viewId, teamId);

        Assert.Equal<string>([$"{UserPrefix}-{userId}-{teamId}"], _harness.Added);
    }

    /// <summary>
    /// A caller who can see every team is keyed on the view instead, so one subscription follows the
    /// user across every team of it rather than needing one per team.
    /// </summary>
    [Fact]
    public async Task JoinUser_ForAViewAdmin_ScopesTheGroupToTheView()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        Visibility(viewId, primaryTeamId: teamId, canViewAllTeams: true, teamId);
        _player.GetUserById(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId, Name = "alice" });

        await Hub.JoinUser(userId, viewId, teamId);

        Assert.Equal<string>([$"{UserPrefix}-{userId}-{viewId}"], _harness.Added);
    }

    [Fact]
    public async Task JoinUser_ForATeamMember_ReportsAnActiveVmOnlyOnThatTeam()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var vmId = Guid.NewGuid();
        Visibility(viewId, primaryTeamId: teamId, canViewAllTeams: false, teamId);
        _player.GetUserById(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId, Name = "alice" });
        _active.GetActiveVirtualMachineForUser(userId)
            .Returns(new ActiveVirtualMachine(vmId, "other-connection", [Guid.NewGuid()], "alice"));

        var result = await Hub.JoinUser(userId, viewId, teamId);

        Assert.Null(result.ActiveVmId);
    }

    /// <summary>
    /// A view admin's answer is not "any team the followed user is on" but "any team in a view I asked
    /// about", which is what <c>IViewService.GetViewIdsForTeams</c> is resolving here. A user active in
    /// another view is not reported as active in this one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JoinUser_ForAViewAdmin_ReportsAnActiveVmOnlyInTheViewAskedAbout(bool sameView)
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var vmId = Guid.NewGuid();
        var activeTeamId = Guid.NewGuid();
        Visibility(viewId, primaryTeamId: teamId, canViewAllTeams: true, teamId);
        _player.GetUserById(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId, Name = "alice" });
        _active.GetActiveVirtualMachineForUser(userId)
            .Returns(new ActiveVirtualMachine(vmId, "other-connection", [activeTeamId], "alice"));
        Guid[] viewIdsTheVmIsActiveIn = sameView ? [viewId] : [Guid.NewGuid()];
        _views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(viewIdsTheVmIsActiveIn);

        var result = await Hub.JoinUser(userId, viewId, teamId);

        Assert.Equal(sameView ? vmId : (Guid?)null, result.ActiveVmId);
    }

    [Fact]
    public async Task JoinUser_ReportsTheLastSeenVmFromTheDatabase()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var vm = VmApiFactory.VsphereVm(teamId);
        var lastSeen = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        await Seed(vm, new VmUserEntity(userId, vm.Id, teamId, lastSeen));

        Visibility(viewId, primaryTeamId: teamId, canViewAllTeams: false, teamId);
        _player.GetUserById(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId, Name = "alice" });

        var result = await Hub.JoinUser(userId, viewId, teamId);

        Assert.Equal(userId, result.UserId);
        Assert.Equal(teamId, result.TeamId);
        Assert.Equal("alice", result.Username);
        Assert.Equal(vm.Id, result.LastVmId);
        Assert.Equal(lastSeen, result.LastSeen);
    }

    [Fact]
    public async Task LeaveUser_RemovesTheUserScopedGroupForEveryGroupId()
    {
        var viewId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        Guid[] groupIds = [Guid.NewGuid(), Guid.NewGuid()];
        GroupIds(viewId, groupIds);

        await Hub.LeaveUser(userId, viewId);

        Assert.Equal<string>(groupIds.Select(x => $"{UserPrefix}-{userId}-{x}"), _harness.Removed);
    }

    /// <summary>
    /// Leaving takes its names from the group id list rather than from the team the caller joined with,
    /// and the two agree: for a view admin the list is the view id, which is what JoinUser used, and for
    /// a team member it is every visible team, which includes the one they joined with. So a member who
    /// followed a user on one team leaves that subscription and, harmlessly, names subscriptions they
    /// never held.
    /// </summary>
    [Fact]
    public async Task LeaveUser_RemovesTheSubscriptionATeamMembersJoinUserAdded()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        Visibility(viewId, primaryTeamId: teamId, canViewAllTeams: false, teamId, otherTeamId);
        GroupIds(viewId, teamId, otherTeamId);
        _player.GetUserById(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId, Name = "alice" });
        var hub = Hub;

        await hub.JoinUser(userId, viewId, teamId);
        await hub.LeaveUser(userId, viewId);

        Assert.Contains($"{UserPrefix}-{userId}-{teamId}", _harness.Removed);
        Assert.Equal<string>([$"{UserPrefix}-{userId}-{teamId}"], _harness.Added);
    }

    #endregion

    #region JoinVm and LeaveVm

    /// <summary>
    /// The per-Vm presence channel, which is what shows who else has a console open on the same machine.
    /// The Vm's own teams decide which views are considered, and the caller's visibility in each of those
    /// views decides whether they are subscribed there at all.
    /// </summary>
    [Fact]
    public async Task JoinVm_AddsThePerVmChannelForTheViewTheCallerSharesWithTheVm()
    {
        var teamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var vm = VmFor(teamId);
        ViewsForTheVm(viewId);
        Visibility(viewId, primaryTeamId: teamId, canViewAllTeams: false, teamId);

        await Hub.JoinVm(vm.Id);

        Assert.Equal<string>([$"{VmUsersPrefix}-{teamId}-{vm.Id}"], _harness.Added);
    }

    /// <summary>
    /// The filter that makes this an authorization decision: a view the Vm belongs to but where none of
    /// the Vm's teams are visible to the caller is skipped, so a caller in one team of a range does not
    /// get the presence feed for another team's machine.
    /// </summary>
    [Fact]
    public async Task JoinVm_ForAVmWithNoTeamTheCallerCanSee_AddsNothing()
    {
        var vm = VmFor(Guid.NewGuid());
        var viewId = Guid.NewGuid();
        ViewsForTheVm(viewId);
        Visibility(viewId, primaryTeamId: Guid.NewGuid(), canViewAllTeams: false, Guid.NewGuid());

        await Hub.JoinVm(vm.Id);

        Assert.Empty(_harness.Added);
    }

    [Fact]
    public async Task JoinVm_ForAVmInNoView_AddsNothing()
    {
        var vm = VmFor(Guid.NewGuid());
        ViewsForTheVm();

        await Hub.JoinVm(vm.Id);

        Assert.Empty(_harness.Added);
    }

    [Fact]
    public async Task JoinVm_ForAViewAdmin_KeysTheChannelOnTheView()
    {
        var teamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var vm = VmFor(teamId);
        ViewsForTheVm(viewId);
        Visibility(viewId, primaryTeamId: teamId, canViewAllTeams: true, teamId);

        await Hub.JoinVm(vm.Id);

        Assert.Equal<string>([$"{VmUsersPrefix}-{viewId}-{vm.Id}"], _harness.Added);
    }

    /// <summary>
    /// For a caller who is not a view admin the hub unions in <em>every</em> team they can see in that
    /// view, not only the ones the Vm is on. So a caller on two teams of a view, looking at a Vm that
    /// belongs to one of them, is also subscribed to the other team's channel for that Vm.
    /// </summary>
    /// <remarks>
    /// Characterized, not fixed. It is over-broad rather than a leak: reaching the union at all needs one
    /// of the Vm's teams to be visible already, and what arrives on the extra channel is the same Vm's
    /// user list keyed to a team the caller is a member of. The narrower rule would be
    /// <c>visibility.TeamIds.Intersect(vm.TeamIds)</c>, and this test is what would say the behaviour
    /// changed.
    /// </remarks>
    [Fact]
    public async Task JoinVm_ForATeamMember_AlsoSubscribesTeamsTheVmIsNotOn()
    {
        var vmTeamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var vm = VmFor(vmTeamId);
        ViewsForTheVm(viewId);
        Visibility(viewId, primaryTeamId: vmTeamId, canViewAllTeams: false, vmTeamId, otherTeamId);

        await Hub.JoinVm(vm.Id);

        Assert.Contains($"{VmUsersPrefix}-{otherTeamId}-{vm.Id}", _harness.Added);
    }

    [Fact]
    public async Task LeaveVm_RemovesExactlyWhatJoinVmAdded()
    {
        var teamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var vm = VmFor(teamId);
        ViewsForTheVm(viewId);
        Visibility(viewId, primaryTeamId: teamId, canViewAllTeams: false, teamId);
        var hub = Hub;

        await hub.JoinVm(vm.Id);
        await hub.LeaveVm(vm.Id);

        Assert.Equal(_harness.Added, _harness.Removed);
    }

    #endregion

    #region Arrangement

    private void GroupIds(Guid viewId, params Guid[] groupIds) =>
        _player.GetGroupIdsForViewAsync(viewId, Arg.Any<CancellationToken>()).Returns(groupIds);

    /// <summary>
    /// The visibility context player.api hands back for a view. Modeled rather than answered per call
    /// site: <c>CanViewAllTeams</c> and the team id set are what the hub branches on, and a substitute
    /// that returned a flat yes or no could not tell a view admin's subscription from a member's.
    /// </summary>
    private void Visibility(Guid viewId, Guid? primaryTeamId, bool canViewAllTeams, params Guid[] teamIds) =>
        _player.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(new VisibilityContext(primaryTeamId, canViewAllTeams, [.. teamIds]));

    private void Teams(Guid viewId, params Team[] teams) =>
        _player.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>()).Returns(teams);

    private void UsersIn(Guid teamId, params User[] users) =>
        _player.GetUsersByTeamId(teamId, Arg.Any<CancellationToken>()).Returns(users);

    /// <summary>
    /// The Vm the hub will find, as <c>IVmService.GetAsync</c> answers - a mapped DTO, not a row, which
    /// is why nothing needs seeding for the join and leave paths.
    /// </summary>
    private VmDto VmFor(params Guid[] teamIds)
    {
        var vm = new VmDto { Id = Guid.NewGuid(), Name = "vm", TeamIds = teamIds };
        _vms.GetAsync(vm.Id, Arg.Any<CancellationToken>()).Returns(vm);

        return vm;
    }

    private void ViewsForTheVm(params Guid[] viewIds) =>
        _views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(viewIds);

    #endregion
}
