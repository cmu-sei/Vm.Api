// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crucible.Common.EntityEvents.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
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
/// The three handlers that announce a Vm to the clients watching it: created, updated and deleted. They
/// are the other end of the names <c>VmHubGroupTests</c> asserts from the joining side - a client that
/// called <c>JoinView</c> is in a group named for a bare view or team guid, and these are what send to it.
///
/// Nothing in the application notices if the two disagree. The handlers are fire and forget: an exception
/// in one is caught and logged by <c>VmContext.PublishEventsAsync</c>, so a name sent to the wrong string
/// costs nothing at the time and leaves every Vm list in every browser silently stale until it is
/// reloaded. That is what makes these worth asserting group by group rather than as "a message was sent".
/// </summary>
/// <remarks>
/// Driven by calling <c>Handle</c> with the notification the application publishes rather than one made up
/// here: the update tests save a real change through the test's own context and take the resulting
/// <c>EntityUpdated</c> off <see cref="DatabaseTestBase.Mediator"/>, so the modified property names are
/// EF's own rather than a guess at what EF would say. <c>EntityEventBroadcastTests</c> covers the step
/// before this one - that a save reaches these handlers at all.
/// </remarks>
public class VmSignalRHandlerTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly IViewService _views = Substitute.For<IViewService>();
    private readonly HubContextHarness<VmHub> _hub = new();

    private VmCreatedSignalRHandler Created => new(Db, TestMapper.Value, _views, _hub.Context);
    private VmUpdatedSignalRHandler Updated => new(Db, TestMapper.Value, _views, _hub.Context);
    private VmDeletedSignalRHandler Deleted => new(Db, TestMapper.Value, _views, _hub.Context);

    #region Which groups an announcement reaches

    public static TheoryData<string> EveryMethod => new()
    {
        VmHubMethods.VmCreated,
        VmHubMethods.VmUpdated,
        VmHubMethods.VmDeleted,
    };

    /// <summary>
    /// All three announcements go to the same places, because all three ask
    /// <c>VmBaseSignalRHandler.GetGroups</c> - the view the Vm's team belongs to, and the team itself.
    /// Both, not either: a client that can see every team of a view joins the view group and a client that
    /// can see only its own team joins the team group, and the two are told by the same send loop.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryMethod))]
    public async Task EveryAnnouncement_TellsTheViewAndTheTeam(string method)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        var vm = await SeedVm(teamId);

        await Announce(method, vm);

        Assert.Equal<string>([viewId.ToString(), teamId.ToString()], _hub.Recipients(method));
    }

    /// <summary>
    /// A Vm shared by two teams of one view is announced to that view once, not once per team. The teams
    /// are still told individually, since a team-scoped subscriber is in no other group.
    /// </summary>
    [Fact]
    public async Task Created_ForTwoTeamsOfOneView_TellsTheViewOnce()
    {
        var (viewId, first, second) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, first, second);
        var vm = await SeedVm(first, second);

        await Created.Handle(new EntityCreated<VmEntity>(vm), Ct);

        Assert.Equal<string>(
            [viewId.ToString(), first.ToString(), second.ToString()],
            _hub.Of(VmHubMethods.VmCreated).SelectMany(x => x.Groups).ToArray());
    }

    [Fact]
    public async Task Created_ForTeamsInTwoViews_TellsBothViews()
    {
        var (firstView, firstTeam) = (Guid.NewGuid(), Guid.NewGuid());
        var (secondView, secondTeam) = (Guid.NewGuid(), Guid.NewGuid());
        InView(firstView, firstTeam);
        InView(secondView, secondTeam);
        var vm = await SeedVm(firstTeam, secondTeam);

        await Created.Handle(new EntityCreated<VmEntity>(vm), Ct);

        Assert.Equal<string>(
            [firstView.ToString(), firstTeam.ToString(), secondView.ToString(), secondTeam.ToString()],
            _hub.Recipients(VmHubMethods.VmCreated));
    }

    /// <summary>
    /// A team player.api reports no view for is still told. Whatever the reason - a view deleted while a
    /// Vm outlived it, or player.api unreachable and the lookup answering null - the team's own
    /// subscribers keep working.
    /// </summary>
    [Fact]
    public async Task Created_ForATeamInNoView_TellsOnlyTheTeam()
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        await Created.Handle(new EntityCreated<VmEntity>(vm), Ct);

        Assert.Equal<string>([teamId.ToString()], _hub.Recipients(VmHubMethods.VmCreated));
    }

    /// <summary>
    /// A Vm on no team is announced to nobody, and that is every create: <c>VmController</c> saves the Vm
    /// and its teams in one <c>SaveChanges</c>, so this is the shape of a Vm created with an empty team
    /// list, or one whose teams were all removed before an update.
    /// </summary>
    /// <remarks>
    /// Characterized rather than treated as a bug, because there is genuinely nobody to tell: no client
    /// can have joined a group for a team the Vm is not on. The contrast worth knowing is
    /// <c>VmDeletedSignalRHandler</c>, which in the same situation tells <em>everyone</em> - see
    /// <see cref="Deleted_ForAVmWhoseTeamsWereNeverLoaded_TellsEveryone"/>.
    /// </remarks>
    [Fact]
    public async Task Created_ForAVmOnNoTeam_TellsNobody()
    {
        var vm = new VmEntity { Name = "orphan" };
        await Seed(vm);

        await Created.Handle(new EntityCreated<VmEntity>(vm), Ct);

        Assert.Empty(_hub.Sends);
    }

    #endregion

    #region What the announcement carries

    /// <summary>
    /// A create sends the Vm and a null property list. The second argument is what the client uses to
    /// decide which fields to refresh, and its absence is how a create is told from an update carrying
    /// every field.
    /// </summary>
    [Fact]
    public async Task Created_SendsTheVmAndNoModifiedProperties()
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        await Created.Handle(new EntityCreated<VmEntity>(vm), Ct);

        var send = Assert.Single(_hub.Of(VmHubMethods.VmCreated));
        Assert.Equal(vm.Id, Assert.IsType<VmDto>(send.Args[0]).Id);
        Assert.Null(send.Args[1]);
    }

    /// <summary>
    /// The property names EF recorded for the save, camel cased. The client matches them against the
    /// serialized field names of the Vm it already holds, which are camel case, so the conversion is the
    /// whole point of the second argument: <c>PowerState</c> would match nothing.
    /// </summary>
    [Fact]
    public async Task Updated_SendsThePropertiesTheSaveActuallyChanged()
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        vm.Name = "renamed";
        vm.PowerState = PowerState.On;
        await Db.SaveChangesAsync(Ct);

        await Updated.Handle(TheUpdate(), Ct);

        var send = Assert.Single(_hub.Of(VmHubMethods.VmUpdated));
        Assert.Equal<string>(["name", "powerState"], (string[])send.Args[1]);
    }

    /// <summary>
    /// Only the first letter is lowered, which is what makes an acronym-led property survive:
    /// <c>IpAddresses</c> has to arrive as <c>ipAddresses</c> and not as <c>ipaddresses</c>.
    /// </summary>
    [Fact]
    public async Task Updated_LowerCasesOnlyTheFirstLetter()
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        vm.IpAddresses = ["10.0.0.1"];
        vm.HasPendingTasks = true;
        await Db.SaveChangesAsync(Ct);

        await Updated.Handle(TheUpdate(), Ct);

        Assert.Equal<string>(
            ["hasPendingTasks", "ipAddresses"],
            ((string[])Assert.Single(_hub.Of(VmHubMethods.VmUpdated)).Args[1]).Order());
    }

    /// <summary>
    /// The Vm in the message is the Vm after the change, not the row the client already had. A client
    /// applies what arrives rather than re-fetching, so sending the pre-change state would leave it
    /// permanently wrong about a Vm that was only ever updated once.
    /// </summary>
    [Fact]
    public async Task Updated_SendsTheStateTheSaveLeftBehind()
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        vm.Name = "renamed";
        await Db.SaveChangesAsync(Ct);

        await Updated.Handle(TheUpdate(), Ct);

        var sent = Assert.IsType<VmDto>(Assert.Single(_hub.Of(VmHubMethods.VmUpdated)).Args[0]);
        Assert.Equal("renamed", sent.Name);
    }

    /// <summary>
    /// The message carries the Vm's whole team list, whichever group it was addressed to. So a subscriber
    /// scoped to one team of a shared Vm learns the ids of the other teams it is on.
    /// </summary>
    /// <remarks>
    /// Characterized, not fixed: it is a team id and nothing else - no name, no membership - and the
    /// projection is the same <c>Vm</c> the REST endpoints return, where the caller has already been
    /// through <c>CanAccessVm</c>. Recorded because the group a message is addressed to is the only
    /// filtering these handlers do, and this is the one thing in the payload that is not about the Vm
    /// itself.
    /// </remarks>
    [Fact]
    public async Task TheVmSent_CarriesEveryTeamItIsOn()
    {
        var (viewId, first, second) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, first);
        var vm = await SeedVm(first, second);

        await Created.Handle(new EntityCreated<VmEntity>(vm), Ct);

        var send = _hub.Of(VmHubMethods.VmCreated).Single(x => x.Groups.Contains(first.ToString()));
        Assert.Equal<Guid>(
            new[] { first, second }.Order(),
            Assert.IsType<VmDto>(send.Args[0]).TeamIds.Order());
    }

    /// <summary>
    /// A delete sends the id alone - one argument where the other two send two - because there is no
    /// longer a row to project. A client that treats every VmHub message the same way would read the
    /// guid as a Vm.
    /// </summary>
    [Fact]
    public async Task Deleted_SendsTheIdAndNothingElse()
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        await Deleted.Handle(new EntityDeleted<VmEntity>(vm), Ct);

        var send = Assert.Single(_hub.Of(VmHubMethods.VmDeleted));
        Assert.Equal(vm.Id, Assert.IsType<Guid>(send.Args[0]));
        Assert.Single(send.Args);
    }

    #endregion

    #region An event that arrives without the Vm's teams

    /// <summary>
    /// The ordinary case for an update: whatever changed the Vm did not need its teams, so the entity in
    /// the event has none and the handler loads them. Every power state change from the pollers looks like
    /// this, and without the load they would announce to nobody.
    /// </summary>
    /// <remarks>
    /// <c>Vm.TeamsLoaded</c> is <c>VmTeams != null &amp;&amp; VmTeams.Count > 0</c>, so "not loaded" and
    /// "on no team" are the same thing to this handler - which is why a genuinely team-less Vm is queried
    /// for its teams on every single announcement.
    /// </remarks>
    [Fact]
    public async Task Updated_WhenTheEventCarriesNoTeams_LoadsThemFromTheDatabase()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        var seeded = await SeedColdly(teamId);

        // Read back without Include, which is what a service that only needed the Vm itself does.
        var vm = await Db.Vms.SingleAsync(x => x.Id == seeded.Id, Ct);
        await Updated.Handle(new EntityUpdated<VmEntity>(vm, ["Name"]), Ct);

        Assert.Equal<string>(
            [viewId.ToString(), teamId.ToString()],
            _hub.Recipients(VmHubMethods.VmUpdated));
    }

    /// <summary>
    /// An entity this context never tracked is attached before it is read from, which is what lets a
    /// handler load the teams of a Vm that reached it from anywhere else.
    /// </summary>
    [Fact]
    public async Task Created_WhenTheEventCarriesADetachedVm_AttachesItFirst()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        var seeded = await SeedColdly(teamId);

        await Created.Handle(new EntityCreated<VmEntity>(new VmEntity { Id = seeded.Id }), Ct);

        Assert.Equal<string>(
            [viewId.ToString(), teamId.ToString()],
            _hub.Recipients(VmHubMethods.VmCreated));
    }

    /// <summary>
    /// When the event does carry the teams, they are what is used and the database is not asked. Shown by
    /// deleting the row first: the announcement still reaches the team, so it came from the entity.
    /// </summary>
    [Fact]
    public async Task Created_WhenTheEventCarriesTheTeams_DoesNotLookThemUp()
    {
        var teamId = Guid.NewGuid();
        var vm = await SeedVm(teamId);

        await using var context = NewContext();
        await context.VmTeams.Where(x => x.VmId == vm.Id).ExecuteDeleteAsync(Ct);

        await Created.Handle(new EntityCreated<VmEntity>(vm), Ct);

        Assert.Equal<string>([teamId.ToString()], _hub.Recipients(VmHubMethods.VmCreated));
    }

    /// <summary>
    /// The delete handler never loads anything - it asks <c>GetGroups</c> straight off the entity - so a Vm
    /// deleted without its teams in hand is announced to <em>every</em> connected client rather than to the
    /// teams that could see it. The production comment says why that is acceptable: the message is only an
    /// id, so there is nothing to leak.
    /// </summary>
    /// <remarks>
    /// <c>VmService.DeleteAsync</c> does include the teams, so what reaches this branch today is a Vm on no
    /// team at all - <c>TeamsLoaded</c> is false for an empty collection as much as for an unloaded one -
    /// along with any delete written later that forgets the <c>Include</c>. Which is what the branch is
    /// for; it is a fallback, and this is the assertion that says it still works.
    /// </remarks>
    [Fact]
    public async Task Deleted_ForAVmWhoseTeamsWereNeverLoaded_TellsEveryone()
    {
        var teamId = Guid.NewGuid();
        InView(Guid.NewGuid(), teamId);
        var seeded = await SeedColdly(teamId);

        var vm = await Db.Vms.SingleAsync(x => x.Id == seeded.Id, Ct);
        await Deleted.Handle(new EntityDeleted<VmEntity>(vm), Ct);

        Assert.Equal<string>(
            [HubContextHarness<VmHub>.Everyone],
            _hub.Recipients(VmHubMethods.VmDeleted));
    }

    /// <summary>
    /// The other side of that fallback: a delete that does know the groups tells them and stops there, so
    /// no client hears about the same delete twice.
    /// </summary>
    [Fact]
    public async Task Deleted_WhenItKnowsTheGroups_DoesNotAlsoTellEveryone()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        var vm = await SeedVm(teamId);

        await Deleted.Handle(new EntityDeleted<VmEntity>(vm), Ct);

        Assert.DoesNotContain(HubContextHarness<VmHub>.Everyone, _hub.Recipients(VmHubMethods.VmDeleted));
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
    /// A saved Vm that <see cref="DatabaseTestBase.Db"/> has never seen, so a test can hand the handler
    /// an entity in the state it really arrives in - untracked, or tracked without its teams.
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
    /// The <c>EntityUpdated</c> the interceptor published for the save the test just made, so that the
    /// modified property names are the ones EF's change tracker produced.
    /// </summary>
    private EntityUpdated<VmEntity> TheUpdate() =>
        Assert.Single(Mediator.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IMediator.Publish))
            .Select(x => x.GetArguments()[0])
            .OfType<EntityUpdated<VmEntity>>());

    private Task Announce(string method, VmEntity vm) => method switch
    {
        VmHubMethods.VmCreated => Created.Handle(new EntityCreated<VmEntity>(vm), Ct),
        VmHubMethods.VmUpdated => Updated.Handle(new EntityUpdated<VmEntity>(vm, ["Name"]), Ct),
        VmHubMethods.VmDeleted => Deleted.Handle(new EntityDeleted<VmEntity>(vm), Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    #endregion
}
