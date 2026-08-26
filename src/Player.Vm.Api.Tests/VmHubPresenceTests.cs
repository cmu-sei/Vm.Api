// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmDto = Player.Vm.Api.Features.Vms.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The publish half of VmHub: SetActiveVirtualMachine, UnsetActiveVirtualMachine and the disconnect that
/// shares its implementation. Between them they are the only writer of the <c>vm_users</c> table and the
/// only caller of the usage log, so this is where those two are driven from.
///
/// What makes this worth testing separately from the joins is that the hub decides here who is *told*
/// rather than who may listen, and the two rules are not the same one. A caller may subscribe to any team
/// of a view they can see, but their own presence is published only to their primary team in each view -
/// so an operator with elevated access to another team's Vm does not appear in that team's user list.
/// The production code says as much in a comment; these tests are what hold it.
///
/// <see cref="ActiveVirtualMachineService"/> is the real one rather than a substitute, because it is what
/// decides which groups the per-Vm user list goes to and because a set followed by an unset has to round
/// trip through the same store to mean anything.
/// </summary>
public class VmHubPresenceTests : DatabaseTestBase
{
    private const string UserPrefix = "ActiveConsoles";
    private const string VmUsersPrefix = "CurrentVirtualMachineUsers";

    private readonly IPlayerService _player = Substitute.For<IPlayerService>();
    private readonly IVmService _vms = Substitute.For<IVmService>();
    private readonly IViewService _views = Substitute.For<IViewService>();
    private readonly IVmUsageLoggingService _usageLog = Substitute.For<IVmUsageLoggingService>();
    private readonly HubHarness _harness = new(Guid.NewGuid(), "alice");

    private readonly ServiceProvider _provider;
    private readonly IActiveVirtualMachineService _active;
    private readonly List<VmContext> _contexts = [];

    /// <remarks>
    /// The real <see cref="ActiveVirtualMachineService"/> resolves <see cref="IViewService"/> out of a
    /// scope of its own rather than taking it as a dependency, which is why there is a container here
    /// rather than a constructor argument. Telemetry is real too and needs nothing: with
    /// <c>GetInfoForTeams</c> answering empty, no meter is written and nothing observes them.
    /// </remarks>
    public VmHubPresenceTests(DatabaseFixture fixture) : base(fixture)
    {
        _views.GetInfoForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns([]);

        _provider = new ServiceCollection()
            .AddSingleton(_views)
            .BuildServiceProvider();

        _active = new ActiveVirtualMachineService(
            _provider.GetRequiredService<IServiceScopeFactory>(), new TelemetryService());
    }

    /// <summary>
    /// A hub over the shared active-machine store and a context of its own.
    /// </summary>
    /// <remarks>
    /// A context per invocation is what production does - the hub is resolved from the connection's
    /// scope for every call - and here it is load bearing rather than incidental. The row write attaches
    /// a fresh <c>VmUser</c> instance on every call, so two calls sharing a change tracker throw on the
    /// second attach for a reason no deployed instance would ever hit.
    /// </remarks>
    private VmHub Hub
    {
        get
        {
            var context = NewContext();
            _contexts.Add(context);

            return _harness.Attach(new VmHub(_active, _usageLog, _views, _player, _vms, context));
        }
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        await _provider.DisposeAsync();
        await base.DisposeAsync();
    }

    #region SetActiveVirtualMachine

    /// <summary>
    /// The three group names one presence change is published to, per id: the followers of this user, the
    /// people watching this Vm, and the followers of the whole group. Every one of them is a name some
    /// join method in <c>VmHubGroupTests</c> adds, which is what makes the pair a contract rather than two
    /// independent string builders.
    /// </summary>
    [Fact]
    public async Task SetActiveVirtualMachine_PublishesToTheUserTheVmAndTheGroupFeeds()
    {
        var team = Guid.NewGuid();
        var view = Guid.NewGuid();
        var vm = await VmOn(team);
        InOneView(view, primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(vm.Id);

        Assert.Equal<string>(
            [
                $"{UserPrefix}-{_harness.UserId}-{team}",
                $"{VmUsersPrefix}-{team}-{vm.Id}",
                $"{UserPrefix}-{team}",
                $"{UserPrefix}-{_harness.UserId}-{view}",
                $"{VmUsersPrefix}-{view}-{vm.Id}",
                $"{UserPrefix}-{view}"
            ],
            _harness.Sends.First(x => x.Method == VmHubMethods.ActiveVirtualMachine).Groups);
    }

    [Fact]
    public async Task SetActiveVirtualMachine_PublishesTheVmTheUserAndTheTeamsItWasOpenedFrom()
    {
        var team = Guid.NewGuid();
        var vm = await VmOn(team);
        InOneView(Guid.NewGuid(), primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(vm.Id);

        var send = _harness.Sends.First(x => x.Method == VmHubMethods.ActiveVirtualMachine);
        Assert.Equal<object>(vm.Id, send.Args[0]);
        Assert.Equal<object>(_harness.UserId, send.Args[1]);
        Assert.IsType<DateTimeOffset>(send.Args[2]);
        Assert.Equal<Guid>([team], (IEnumerable<Guid>)send.Args[3]);
    }

    /// <summary>
    /// The rule the production comment exists for. A caller whose primary team is one thing and who can
    /// reach the Vm because they can see another team of the view is published to their own primary team
    /// only - naming the Vm's team here would announce a non-member to that team's user list.
    /// </summary>
    [Fact]
    public async Task SetActiveVirtualMachine_PublishesToThePrimaryTeamNotTheVmsTeam()
    {
        var primary = Guid.NewGuid();
        var vmTeam = Guid.NewGuid();
        var view = Guid.NewGuid();
        var vm = await VmOn(vmTeam);
        InOneView(view, primaryTeamId: primary, visibleTeamIds: [primary, vmTeam]);

        await Hub.SetActiveVirtualMachine(vm.Id);

        var recipients = _harness.Recipients(VmHubMethods.ActiveVirtualMachine);
        Assert.Contains($"{UserPrefix}-{primary}", recipients);
        Assert.DoesNotContain($"{UserPrefix}-{vmTeam}", recipients);
    }

    /// <summary>
    /// A view where none of the Vm's teams are visible is skipped, so a Vm shared between two views does
    /// not leak its user's presence into the one they have nothing to do with.
    /// </summary>
    [Fact]
    public async Task SetActiveVirtualMachine_SkipsAViewWhereNoTeamOfTheVmIsVisible()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var team = Guid.NewGuid();
        var vm = await VmOn(team);
        ViewsForTheVm(mine, theirs);
        Visibility(mine, primaryTeamId: team, canViewAllTeams: false, team);
        Visibility(theirs, primaryTeamId: Guid.NewGuid(), canViewAllTeams: false, Guid.NewGuid());

        await Hub.SetActiveVirtualMachine(vm.Id);

        var recipients = _harness.Recipients(VmHubMethods.ActiveVirtualMachine);
        Assert.Contains($"{UserPrefix}-{mine}", recipients);
        Assert.DoesNotContain($"{UserPrefix}-{theirs}", recipients);
    }

    /// <summary>
    /// No primary team in the view means nothing is published there at all, not even to the view group.
    /// That is what a caller who holds a permission over the view but is on none of its teams looks like.
    /// </summary>
    [Fact]
    public async Task SetActiveVirtualMachine_ForAViewWithNoPrimaryTeam_PublishesNothing()
    {
        var view = Guid.NewGuid();
        var team = Guid.NewGuid();
        var vm = await VmOn(team);
        ViewsForTheVm(view);
        Visibility(view, primaryTeamId: null, canViewAllTeams: true, team);

        await Hub.SetActiveVirtualMachine(vm.Id);

        Assert.Empty(_harness.Recipients(VmHubMethods.ActiveVirtualMachine));
    }

    /// <summary>
    /// The second message: the list of who has this Vm open, sent once per group, which is what the
    /// per-Vm channel <c>JoinVm</c> subscribes to carries.
    /// </summary>
    /// <remarks>
    /// Asserted as a set. The groups come out of a dictionary the active-machine store builds, so their
    /// order is whatever the hash buckets give and is nothing a client could rely on.
    /// </remarks>
    [Fact]
    public async Task SetActiveVirtualMachine_PublishesTheVmsUserListToEachGroupsChannel()
    {
        var team = Guid.NewGuid();
        var view = Guid.NewGuid();
        var vm = await VmOn(team);
        InOneView(view, primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(vm.Id);

        var channels = _harness.Recipients(VmHubMethods.CurrentVirtualMachineUsers);
        Assert.Equal(2, channels.Count);
        Assert.Contains($"{VmUsersPrefix}-{team}-{vm.Id}", channels);
        Assert.Contains($"{VmUsersPrefix}-{view}-{vm.Id}", channels);
        Assert.All(
            _harness.Sends.Where(x => x.Method == VmHubMethods.CurrentVirtualMachineUsers),
            send =>
            {
                Assert.Equal<object>(vm.Id, send.Args[0]);
                Assert.Equal<string>(["alice"], (IEnumerable<string>)send.Args[1]);
            });
    }

    [Fact]
    public async Task SetActiveVirtualMachine_WritesAUsageLogEntryForThePrimaryTeam()
    {
        var team = Guid.NewGuid();
        var vm = await VmOn(team);
        InOneView(Guid.NewGuid(), primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(vm.Id);

        await _usageLog.Received(1).CreateVmLogEntry(
            _harness.UserId,
            vm.Id,
            Arg.Is<IEnumerable<Guid>>(x => x.SequenceEqual(new[] { team })),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The <c>vm_users</c> row, which is what the "last seen" column in a view's user list is read from.
    /// This is its only writer, and it is keyed on the primary team for the same reason the broadcast is.
    /// </summary>
    [Fact]
    public async Task SetActiveVirtualMachine_ForAUserWithNoRow_InsertsOne()
    {
        var primary = Guid.NewGuid();
        var vmTeam = Guid.NewGuid();
        var vm = await VmOn(vmTeam);
        InOneView(Guid.NewGuid(), primaryTeamId: primary, visibleTeamIds: [primary, vmTeam]);

        await Hub.SetActiveVirtualMachine(vm.Id);

        await using var context = NewContext();
        var row = await context.VmUsers.SingleAsync(Ct);
        Assert.Equal(_harness.UserId, row.UserId);
        Assert.Equal(primary, row.TeamId);
        Assert.Equal(vm.Id, row.LastVmId);
        Assert.NotEqual(default, row.LastSeen);
    }

    /// <summary>
    /// The insert is the fallback: the write is an update first and only becomes an add when that finds
    /// no row. A second console on a second Vm has to move the existing row rather than add another,
    /// because (UserId, TeamId) is the key and a second insert would fail.
    /// </summary>
    [Fact]
    public async Task SetActiveVirtualMachine_ForAUserWithARow_MovesItToTheNewVm()
    {
        var team = Guid.NewGuid();
        var first = await VmOn(team);
        var second = await VmOn(team);
        InOneView(Guid.NewGuid(), primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(first.Id);
        await Hub.SetActiveVirtualMachine(second.Id);

        await using var context = NewContext();
        var row = await context.VmUsers.SingleAsync(Ct);
        Assert.Equal(second.Id, row.LastVmId);
    }

    /// <summary>
    /// One row per primary team, so a user reachable through two views gets a row in each - which is what
    /// makes the column readable per team rather than per user.
    /// </summary>
    [Fact]
    public async Task SetActiveVirtualMachine_WithAPrimaryTeamInTwoViews_WritesARowForEach()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var viewA = Guid.NewGuid();
        var viewB = Guid.NewGuid();
        var vm = await VmOn(teamA, teamB);
        ViewsForTheVm(viewA, viewB);
        Visibility(viewA, primaryTeamId: teamA, canViewAllTeams: false, teamA);
        Visibility(viewB, primaryTeamId: teamB, canViewAllTeams: false, teamB);

        await Hub.SetActiveVirtualMachine(vm.Id);

        await using var context = NewContext();
        var teams = await context.VmUsers.Select(x => x.TeamId).ToArrayAsync(Ct);
        Assert.Equal(2, teams.Length);
        Assert.Contains(teamA, teams);
        Assert.Contains(teamB, teams);
    }

    #endregion

    #region UnsetActiveVirtualMachine and OnDisconnectedAsync

    /// <summary>
    /// Unsetting is the same broadcast with a null Vm, to the same groups, which is what lets a client
    /// clear the icon it drew when the set arrived.
    /// </summary>
    [Fact]
    public async Task Unset_PublishesANullVmToTheGroupsSetPublishedTo()
    {
        var team = Guid.NewGuid();
        var view = Guid.NewGuid();
        var vm = await VmOn(team);
        InOneView(view, primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(vm.Id);
        await Hub.UnsetActiveVirtualMachine();

        var sends = _harness.Sends.Where(x => x.Method == VmHubMethods.ActiveVirtualMachine).ToArray();
        var unset = Assert.Single(sends, x => x.Args[0] is null);
        Assert.Equal<object>(_harness.UserId, unset.Args[1]);
        Assert.Null(unset.Args[2]);
        Assert.Equal<string>(sends[0].Groups.Order().ToArray(), unset.Groups.Order().ToArray());
    }

    /// <summary>
    /// Unsetting takes the teams from what was recorded when the console was opened, not from the
    /// caller's visibility now. That is what makes the two messages address the same groups even if the
    /// caller's teams changed in between - and it is why the store has to be the real one here.
    /// </summary>
    [Fact]
    public async Task Unset_UsesTheTeamsRecordedWhenTheConsoleWasOpened()
    {
        var team = Guid.NewGuid();
        var view = Guid.NewGuid();
        var vm = await VmOn(team);
        InOneView(view, primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(vm.Id);
        Visibility(view, primaryTeamId: Guid.NewGuid(), canViewAllTeams: false, Guid.NewGuid());
        await Hub.UnsetActiveVirtualMachine();

        Assert.Contains(
            $"{UserPrefix}-{_harness.UserId}-{team}",
            _harness.Sends.Last(x => x.Method == VmHubMethods.ActiveVirtualMachine).Groups);
    }

    [Fact]
    public async Task Unset_ClosesTheUsageLogEntry()
    {
        var team = Guid.NewGuid();
        var vm = await VmOn(team);
        InOneView(Guid.NewGuid(), primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(vm.Id);
        await Hub.UnsetActiveVirtualMachine();

        await _usageLog.Received(1).CloseVmLogEntry(_harness.UserId, vm.Id, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Nothing active means nothing published and no log entry closed. This is the ordinary case for a
    /// connection that only ever watched a view, and every disconnect takes this path.
    /// </summary>
    [Fact]
    public async Task Unset_WithNothingActive_PublishesNothingAndClosesNothing()
    {
        await Hub.UnsetActiveVirtualMachine();

        Assert.Empty(_harness.Sends);
        await _usageLog.DidNotReceive().CloseVmLogEntry(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A dropped connection has to clear the presence too, or a closed browser tab leaves the user shown
    /// as watching a Vm forever. It is the same code path as the explicit unset, which is what this pins:
    /// a disconnect that stopped calling it would look like a working connection to every other test.
    /// </summary>
    [Fact]
    public async Task OnDisconnected_ClearsThePresenceAndClosesTheLogEntry()
    {
        var team = Guid.NewGuid();
        var vm = await VmOn(team);
        InOneView(Guid.NewGuid(), primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(vm.Id);
        await Hub.OnDisconnectedAsync(null);

        Assert.Null(_harness.Sends.Last(x => x.Method == VmHubMethods.ActiveVirtualMachine).Args[0]);
        Assert.Null(_active.GetActiveVirtualMachineForUser(_harness.UserId));
        await _usageLog.Received(1).CloseVmLogEntry(_harness.UserId, vm.Id, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The unset only fires for the connection that set the presence. A second tab of the same user
    /// closing must not clear the console the first one still has open, which is what the connection id
    /// check in <c>UnsetActiveVirtualMachineForUser</c> is for.
    /// </summary>
    [Fact]
    public async Task OnDisconnected_FromAnotherConnectionOfTheSameUser_ClearsNothing()
    {
        var team = Guid.NewGuid();
        var vm = await VmOn(team);
        InOneView(Guid.NewGuid(), primaryTeamId: team, visibleTeamIds: [team]);

        await Hub.SetActiveVirtualMachine(vm.Id);

        var otherTab = new HubHarness(_harness.UserId, "alice", "another-tab");
        await otherTab.Attach(new VmHub(_active, _usageLog, _views, _player, _vms, Db))
            .OnDisconnectedAsync(null);

        Assert.Empty(otherTab.Sends);
        Assert.Equal(vm.Id, _active.GetActiveVirtualMachineForUser(_harness.UserId).VmId);
    }

    #endregion

    #region Arrangement

    /// <summary>
    /// A Vm on the given teams, both as a row and as the answer <c>IVmService.GetAsync</c> gives.
    /// </summary>
    /// <remarks>
    /// The row is not optional even for a test that only asserts who was told, because every set also
    /// writes <c>vm_users</c> and <c>last_vm_id</c> is a real foreign key: without it the write fails
    /// after the broadcast has already gone out. Production cannot reach that state - the Vm the hub is
    /// handed came from the same table - so it is arranged away here rather than characterized.
    /// Only the first team gets a membership row; the rest are teams the caller can see it through,
    /// which is a Player fact and not a local one.
    /// </remarks>
    private async Task<VmDto> VmOn(params Guid[] teamIds)
    {
        var entity = VmApiFactory.VsphereVm(teamIds[0]);
        await Seed(entity);

        var vm = new VmDto { Id = entity.Id, Name = entity.Name, TeamIds = teamIds, IpAddresses = [] };
        _vms.GetAsync(vm.Id, Arg.Any<CancellationToken>()).Returns(vm);

        return vm;
    }

    private void ViewsForTheVm(params Guid[] viewIds) =>
        _views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(viewIds);

    private void Visibility(Guid viewId, Guid? primaryTeamId, bool canViewAllTeams, params Guid[] teamIds) =>
        _player.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(new VisibilityContext(primaryTeamId, canViewAllTeams, [.. teamIds]));

    /// <summary>The common arrangement: the Vm is in one view, and the caller sees it there.</summary>
    private void InOneView(Guid viewId, Guid primaryTeamId, Guid[] visibleTeamIds)
    {
        ViewsForTheVm(viewId);
        Visibility(viewId, primaryTeamId, canViewAllTeams: false, visibleTeamIds);
    }

    #endregion
}
