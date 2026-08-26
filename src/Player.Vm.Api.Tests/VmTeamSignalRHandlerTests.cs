// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crucible.Common.EntityEvents.Events;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms.EventHandlers;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmDto = Player.Vm.Api.Features.Vms.Vm;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The two handlers that announce a Vm gaining or losing a team. A team is how a Vm becomes visible, so
/// these say "this Vm has appeared" and "this Vm has gone" to a set of clients for which nothing about the
/// Vm itself changed - and they reuse the <c>VmCreated</c> and <c>VmDeleted</c> method names to say it.
/// </summary>
/// <remarks>
/// <para>
/// Where <c>VmSignalRHandlerTests</c> covers announcements about a Vm, these are about one row of the join
/// table, and the difference shows in the view group. A view group holds clients that can see every team
/// of a view, so adding a Vm to a second team of the same view changes nothing for them: both handlers
/// look at the Vm's other teams and drop the view send when one of them resolves to the same view. That
/// suppression is the only real logic in either handler, and it is the reason both need the Vm's whole
/// team list rather than just the row that changed.
/// </para>
/// <para>
/// Built the same way as <c>VmSignalRHandlerTests</c> - real database, recording hub context, substituted
/// view service - and the notifications are constructed here rather than saved for, because what a real
/// save publishes is <c>EntityEventBroadcastTests</c>'s subject.
/// </para>
/// </remarks>
public class VmTeamSignalRHandlerTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly IViewService _views = Substitute.For<IViewService>();
    private readonly HubContextHarness<VmHub> _hub = new();

    private VmTeamCreatedSignalRHandler Created => new(Db, TestMapper.Value, _views, _hub.Context);
    private VmTeamDeletedSignalRHandler Deleted => new(Db, TestMapper.Value, _views, _hub.Context);

    #region Who hears that a team gained or lost a Vm

    /// <summary>
    /// A Vm added to a team is announced to that team and to the view the team is in. The view send is
    /// what makes a Vm appear in the list of a client watching the whole view, which is how the Angular
    /// client subscribes for an admin.
    /// </summary>
    [Fact]
    public async Task Created_TellsTheTeamAndItsView()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        var vm = await SeedVm(teamId);

        await Created.Handle(new EntityCreated<VmTeam>(TeamRow(vm, teamId)), Ct);

        Assert.Equal<string>(
            [viewId.ToString(), teamId.ToString()],
            _hub.Recipients(VmHubMethods.VmCreated));
    }

    /// <summary>
    /// A Vm removed from a team is announced the same way, as a delete. A client in the view group loses
    /// the Vm from its list even though the Vm still exists - correctly, since with that team gone the
    /// view can no longer see it.
    /// </summary>
    [Fact]
    public async Task Deleted_TellsTheTeamAndItsView()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        var vm = await SeedVm();

        await Deleted.Handle(new EntityDeleted<VmTeam>(TeamRow(vm, teamId)), Ct);

        Assert.Equal<string>(
            [viewId.ToString(), teamId.ToString()],
            _hub.Recipients(VmHubMethods.VmDeleted));
    }

    /// <summary>
    /// The suppression that both handlers exist to do: the Vm was already on another team of this view, so
    /// the view group has it in its list already and is not told again. Only the team that changed is.
    /// </summary>
    [Fact]
    public async Task Created_WhenAnotherTeamOfTheVmIsInTheSameView_TellsOnlyTheTeam()
    {
        var (viewId, joining, existing) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, joining, existing);
        var vm = await SeedVm(joining, existing);

        await Created.Handle(new EntityCreated<VmTeam>(TeamRow(vm, joining)), Ct);

        Assert.Equal<string>([joining.ToString()], _hub.Recipients(VmHubMethods.VmCreated));
    }

    /// <summary>
    /// The delete half of it, and the sharper case: the Vm is still on another team of the view, so
    /// telling the view would make the Vm vanish from a list that should still show it.
    /// </summary>
    [Fact]
    public async Task Deleted_WhenTheVmIsStillOnAnotherTeamOfTheSameView_TellsOnlyTheTeam()
    {
        var (viewId, leaving, remaining) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, leaving, remaining);
        var vm = await SeedVm(remaining);

        await Deleted.Handle(new EntityDeleted<VmTeam>(TeamRow(vm, leaving)), Ct);

        Assert.Equal<string>([leaving.ToString()], _hub.Recipients(VmHubMethods.VmDeleted));
    }

    /// <summary>
    /// Another team in a <em>different</em> view does not suppress anything, so both views hear what
    /// concerns them. Only a shared view is a reason to stay quiet.
    /// </summary>
    [Fact]
    public async Task Created_WhenAnotherTeamOfTheVmIsInAnotherView_StillTellsTheView()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var (otherView, otherTeam) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        InView(otherView, otherTeam);
        var vm = await SeedVm(teamId, otherTeam);

        await Created.Handle(new EntityCreated<VmTeam>(TeamRow(vm, teamId)), Ct);

        Assert.Equal<string>(
            [viewId.ToString(), teamId.ToString()],
            _hub.Recipients(VmHubMethods.VmCreated));
    }

    /// <summary>
    /// A team in no view is still told, as everywhere else in these handlers: whatever player.api knows or
    /// does not know about the team, its own subscribers are the ones the change is about.
    /// </summary>
    [Theory]
    [InlineData(VmHubMethods.VmCreated)]
    [InlineData(VmHubMethods.VmDeleted)]
    public async Task EitherAnnouncement_ForATeamInNoView_TellsOnlyTheTeam(string method)
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        await Announce(method, TeamRow(vm, teamId));

        Assert.Equal<string>([teamId.ToString()], _hub.Recipients(method));
    }

    #endregion

    #region What the announcement carries

    /// <summary>
    /// The create sends the Vm and nothing else - one argument where <c>VmCreatedSignalRHandler</c> sends
    /// two under the same <c>VmCreated</c> name.
    /// </summary>
    /// <remarks>
    /// Characterized: a client's handler for <c>VmCreated</c> has to tolerate both shapes, because both
    /// handlers fire for the same save - see
    /// <c>EntityEventBroadcastTests.CreatingAVmOnATeam_AnnouncesItTwiceToEachGroup</c>. SignalR passes a
    /// missing argument as the parameter's default, so a two-parameter client method reads the absent
    /// modified-property list as null, which is what a create means anyway.
    /// </remarks>
    [Fact]
    public async Task Created_SendsTheVmAndNothingElse()
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        await Created.Handle(new EntityCreated<VmTeam>(TeamRow(vm, teamId)), Ct);

        var send = Assert.Single(_hub.Of(VmHubMethods.VmCreated));
        Assert.Equal(vm.Id, Assert.IsType<VmDto>(Assert.Single(send.Args)).Id);
    }

    /// <summary>
    /// The delete sends the id of the Vm, not of the team, because it is the Vm the client has to drop
    /// from its list. The two guids in the event are easy to confuse and a client cannot tell them apart.
    /// </summary>
    [Fact]
    public async Task Deleted_SendsTheVmIdAndNotTheTeamId()
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        await Deleted.Handle(new EntityDeleted<VmTeam>(TeamRow(vm, teamId)), Ct);

        var send = Assert.Single(_hub.Of(VmHubMethods.VmDeleted));
        Assert.Equal(vm.Id, Assert.IsType<Guid>(Assert.Single(send.Args)));
    }

    #endregion

    #region An event that arrives without its Vm

    /// <summary>
    /// The ordinary case, and what both of the application's own team changes look like:
    /// <c>VmService.AddToTeamAsync</c> saves a bare join-table row and <c>RemoveFromTeamAsync</c> deletes
    /// one, neither with the Vm in hand. So the handler loads the Vm and its teams, and without the
    /// <c>Include</c> the suppression loop would see no other teams and tell the view about a Vm it
    /// already has.
    /// </summary>
    /// <remarks>
    /// The load runs without a cancellation token - <c>FirstOrDefaultAsync()</c> with no argument, in both
    /// handlers - so a cancelled request still waits for the query. Left as it is: it is one query by
    /// primary key, and a handler is fire and forget by then anyway, with nobody waiting on the save.
    /// </remarks>
    [Fact]
    public async Task Created_WhenTheEventCarriesNoVm_LoadsItWithItsTeams()
    {
        var (viewId, joining, existing) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, joining, existing);
        var vm = await SeedColdly(joining, existing);

        await Created.Handle(new EntityCreated<VmTeam>(new VmTeam(joining, vm.Id)), Ct);

        Assert.Equal<string>([joining.ToString()], _hub.Recipients(VmHubMethods.VmCreated));
    }

    /// <summary>
    /// A create for a Vm that no longer exists tells nobody, which is the safe answer: there is nothing to
    /// send, since the message is the Vm itself.
    /// </summary>
    [Fact]
    public async Task Created_WhenTheVmIsGone_TellsNobody()
    {
        var teamId = Guid.NewGuid();
        InView(Guid.NewGuid(), teamId);

        await Created.Handle(new EntityCreated<VmTeam>(new VmTeam(teamId, Guid.NewGuid())), Ct);

        Assert.Empty(_hub.Sends);
    }

    /// <summary>
    /// The delete instead still tells the team, and it can: the message is a Vm id, which the event
    /// carries, so a Vm that has since gone is no reason to keep quiet. A team-scoped client would
    /// otherwise be left holding a Vm nothing will ever tell it about again.
    /// </summary>
    /// <remarks>
    /// The asymmetry with <see cref="Created_WhenTheVmIsGone_TellsNobody"/> is in the shape of the
    /// production code - the delete's null check wraps only the view send, the create's wraps everything -
    /// and it happens to be the right way round. What it does not cover is the view group, which hears
    /// nothing at all in this case; a client watching a whole view keeps the Vm. Defensive rather than
    /// ordinary: a real cascade leaves the Vm reference on the event intact, as
    /// <c>EntityEventBroadcastTests.DeletingAVm_AnnouncesTheDeleteForTheVmAndForEachTeamItWasOn</c> shows,
    /// so this is the shape of an event whose Vm something else removed first.
    /// </remarks>
    [Fact]
    public async Task Deleted_WhenTheVmIsGone_StillTellsTheTeam()
    {
        var (teamId, vmId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(Guid.NewGuid(), teamId);

        await Deleted.Handle(new EntityDeleted<VmTeam>(new VmTeam(teamId, vmId)), Ct);

        var send = Assert.Single(_hub.Of(VmHubMethods.VmDeleted));
        Assert.Equal<string>([teamId.ToString()], send.Groups);
        Assert.Equal(vmId, Assert.IsType<Guid>(Assert.Single(send.Args)));
    }

    #endregion

    #region Arrangement

    /// <summary>Puts every named team in one view, as player.api would report it.</summary>
    private void InView(Guid viewId, params Guid[] teamIds)
    {
        foreach (var teamId in teamIds)
        {
            _views.GetViewIdForTeam(teamId, Arg.Any<CancellationToken>()).Returns(viewId);
        }
    }

    /// <summary>A saved Vm on the given teams, tracked by <see cref="DatabaseTestBase.Db"/>.</summary>
    private async Task<VmEntity> SeedVm(params Guid[] teamIds)
    {
        var vm = new VmEntity
        {
            Name = "vm",
            VmTeams = teamIds.Select(x => new VmTeam { TeamId = x }).ToList(),
        };

        await Seed(vm);

        return vm;
    }

    /// <summary>
    /// A saved Vm that <see cref="DatabaseTestBase.Db"/> has never seen, so that a handler given a bare
    /// join-table row has to find it in the database.
    /// </summary>
    private async Task<VmEntity> SeedColdly(params Guid[] teamIds)
    {
        await using var context = NewContext();

        var vm = new VmEntity
        {
            Name = "vm",
            VmTeams = teamIds.Select(x => new VmTeam { TeamId = x }).ToList(),
        };

        context.Add(vm);
        await context.SaveChangesAsync(Ct);

        return vm;
    }

    /// <summary>
    /// The row the event carries, with its Vm attached - the state an event raised alongside the Vm's own
    /// changes arrives in. <see cref="Created_WhenTheEventCarriesNoVm_LoadsItWithItsTeams"/> is the other
    /// state.
    /// </summary>
    /// <remarks>
    /// Built rather than taken from <c>vm.VmTeams</c>, because a delete's row is one that is no longer
    /// there: the team a Vm just left is the whole subject of <c>VmTeamDeletedSignalRHandler</c>, and it
    /// is not in the collection the handler reads.
    /// </remarks>
    private static VmTeam TeamRow(VmEntity vm, Guid teamId) =>
        new(teamId, vm.Id) { Vm = vm };

    private Task Announce(string method, VmTeam team) => method switch
    {
        VmHubMethods.VmCreated => Created.Handle(new EntityCreated<VmTeam>(team), Ct),
        VmHubMethods.VmDeleted => Deleted.Handle(new EntityDeleted<VmTeam>(team), Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    #endregion
}
