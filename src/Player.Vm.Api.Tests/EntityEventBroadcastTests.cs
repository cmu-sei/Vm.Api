// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The path from a save to a broadcast, with nothing stubbed in between: a real <c>SaveChanges</c>, the
/// entity event interceptor, real MediatR resolving the handlers the application registered, and the five
/// handlers themselves. What the other two handler classes assert about a single <c>Handle</c> call, this
/// asserts about the events the application actually raises.
/// </summary>
/// <remarks>
/// <para>
/// Worth its own class because the wiring is the part nobody can see. The handlers are found by assembly
/// scan, published by a mediator resolved off <c>VmContext.ServiceProvider</c>, and every exception one
/// throws is caught and logged by <c>PublishEventsAsync</c> - so a handler that stopped being registered,
/// or one that threw on every event, would leave every test of the save itself green.
/// </para>
/// <para>
/// The provider here is built by hand rather than taken from <see cref="VmApiFactory"/>, because what the
/// handlers need is small - a mapper, a view service, a hub context and the context that raised the event -
/// and a substituted view service is the only way to decide what view a team is in. The registrations
/// mirror <c>Startup</c>: <c>AddMediatR</c> over the application assembly, and the context scoped so the
/// handlers read the same one that saved.
/// </para>
/// </remarks>
public class EntityEventBroadcastTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly IViewService _views = Substitute.For<IViewService>();
    private readonly HubContextHarness<VmHub> _hub = new();

    private ServiceProvider _provider;
    private VmContext _app;

    /// <summary>
    /// A context whose saves reach the real handlers, alongside the inherited <see cref="Db"/> whose saves
    /// only reach the substituted mediator. Tests seed through <see cref="Db"/> and act through this one.
    /// </summary>
    private VmContext App => _app;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssemblies(typeof(Player.Vm.Api.Startup).Assembly));
        services.AddSingleton(_views);
        services.AddSingleton(_hub.Context);
        services.AddSingleton(TestMapper.Value);

        // Resolved lazily: the context needs the provider it will publish through, so one of the two has
        // to be built first. Production has the same cycle and breaks it the same way, with a request
        // scope that already holds the context.
        services.AddScoped(_ => _app);

        _provider = services.BuildServiceProvider();
        _app = Session.CreateContext(_provider);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>
    /// Creating a Vm on a team announces it twice to each group: once by
    /// <c>VmCreatedSignalRHandler</c> for the Vm row and once by <c>VmTeamCreatedSignalRHandler</c> for
    /// the join-table row, both under <c>VmCreated</c> and with different argument counts. One
    /// <c>SaveChanges</c> raises both events, because <c>VmService.CreateAsync</c> writes the Vm and its
    /// teams together.
    /// </summary>
    /// <remarks>
    /// Characterized, not fixed. It is idempotent at the client - the second message carries the same Vm -
    /// and the two handlers cannot easily know about each other, since each is told about its own row. What
    /// it costs is a doubled message per group on every create, and it is the reason a test that counted
    /// only "was something sent" would say nothing useful here.
    /// </remarks>
    [Fact]
    public async Task CreatingAVmOnATeam_AnnouncesItTwiceToEachGroup()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);

        App.Add(new VmEntity { Name = "vm", VmTeams = [new VmTeam { TeamId = teamId }] });
        await App.SaveChangesAsync(Ct);

        var sends = _hub.Of(VmHubMethods.VmCreated);
        Assert.Equal<string>(
            [viewId.ToString(), teamId.ToString()],
            _hub.Recipients(VmHubMethods.VmCreated));
        Assert.Equal(4, sends.Count);
        Assert.Equal(2, sends.Count(x => x.Args.Length == 2));
    }

    /// <summary>
    /// An update reaches the handler with the properties the save changed and the teams it did not load,
    /// which is the shape of every announcement the pollers cause: they read a Vm by id, set its power
    /// state and save.
    /// </summary>
    [Fact]
    public async Task UpdatingAVm_AnnouncesTheChangedPropertiesToEachGroup()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        var seeded = await SeedVm(teamId);

        var vm = await App.Vms.SingleAsync(x => x.Id == seeded.Id, Ct);
        vm.PowerState = PowerState.On;
        await App.SaveChangesAsync(Ct);

        var send = Assert.Single(_hub.Of(VmHubMethods.VmUpdated), x => x.Groups.Contains(teamId.ToString()));
        Assert.Equal<string>(["powerState"], (string[])send.Args[1]);
        Assert.Equal<string>(
            [viewId.ToString(), teamId.ToString()],
            _hub.Recipients(VmHubMethods.VmUpdated));
    }

    /// <summary>
    /// Deleting a Vm the way the application deletes one - loaded with its teams, as
    /// <c>VmService.DeleteAsync</c> does - announces the delete to each group twice: once for the Vm and
    /// once for each team row the cascade removed.
    /// </summary>
    [Fact]
    public async Task DeletingAVm_AnnouncesTheDeleteForTheVmAndForEachTeamItWasOn()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        var seeded = await SeedVm(teamId);

        var vm = await App.Vms.Include(x => x.VmTeams).SingleAsync(x => x.Id == seeded.Id, Ct);
        App.Remove(vm);
        await App.SaveChangesAsync(Ct);

        Assert.Equal<string>(
            [viewId.ToString(), teamId.ToString()],
            _hub.Recipients(VmHubMethods.VmDeleted));
        Assert.Equal(4, _hub.Of(VmHubMethods.VmDeleted).Count);
        Assert.All(_hub.Sends, x => Assert.Equal(seeded.Id, x.Args[0]));
    }

    /// <summary>
    /// Adding a team to an existing Vm is announced as a create, because for the clients of that team the
    /// Vm has just appeared. Only the join-table row changed, so only one of the five handlers has
    /// anything to say.
    /// </summary>
    [Fact]
    public async Task AddingATeamToAnExistingVm_AnnouncesACreateToThatTeamAndItsView()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, teamId);
        var seeded = await SeedVm();

        App.Add(new VmTeam(teamId, seeded.Id));
        await App.SaveChangesAsync(Ct);

        Assert.Equal<string>(
            [viewId.ToString(), teamId.ToString()],
            _hub.Recipients(VmHubMethods.VmCreated));
        Assert.Equal(2, _hub.Sends.Count);
    }

    /// <summary>
    /// Removing one of two teams that share a view announces the delete to that team and not to the view,
    /// which is the suppression <c>VmTeamDeletedSignalRHandler</c> exists for, seen through a real save:
    /// the view can still reach the Vm through the team that remains.
    /// </summary>
    [Fact]
    public async Task RemovingOneOfTwoTeamsInAView_AnnouncesTheDeleteToThatTeamOnly()
    {
        var (viewId, leaving, remaining) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InView(viewId, leaving, remaining);
        var seeded = await SeedVm(leaving, remaining);

        App.Remove(await App.VmTeams.SingleAsync(x => x.VmId == seeded.Id && x.TeamId == leaving, Ct));
        await App.SaveChangesAsync(Ct);

        Assert.Equal<string>([leaving.ToString()], _hub.Recipients(VmHubMethods.VmDeleted));
    }

    /// <summary>
    /// A handler that throws does not fail the save: <c>PublishEventsAsync</c> catches and logs each
    /// event's exception and moves on to the next. The row is written and the clients are never told, with
    /// nothing but a log line to say so.
    /// </summary>
    /// <remarks>
    /// This is what makes the group names worth asserting directly. The application's own signal that a
    /// broadcast went wrong is a log entry, so the failure a user sees is a Vm list that is quietly out of
    /// date until the page is reloaded - which no test of a request could catch. A view service that
    /// throws is a realistic cause: it calls player.api over HTTP.
    /// </remarks>
    [Fact]
    public async Task WhenAHandlerThrows_TheSaveStillSucceedsAndNobodyIsTold()
    {
        var teamId = Guid.NewGuid();
        _views.GetViewIdForTeam(teamId, Arg.Any<CancellationToken>())
            .Returns<Guid?>(_ => throw new InvalidOperationException("player.api is down"));

        App.Add(new VmEntity { Name = "vm", VmTeams = [new VmTeam { TeamId = teamId }] });
        await App.SaveChangesAsync(Ct);

        Assert.Empty(_hub.Sends);
        await using var context = NewContext();
        Assert.Equal("vm", (await context.Vms.SingleAsync(Ct)).Name);
    }

    #region Arrangement

    /// <summary>Puts every named team in one view, as player.api would report it.</summary>
    private void InView(Guid viewId, params Guid[] teamIds)
    {
        foreach (var teamId in teamIds)
        {
            _views.GetViewIdForTeam(teamId, Arg.Any<CancellationToken>()).Returns(viewId);
        }
    }

    /// <summary>
    /// A saved Vm on the given teams, written through <see cref="DatabaseTestBase.Db"/> so that no handler
    /// hears about the arrangement - only about what the test then does through <see cref="App"/>.
    /// </summary>
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

    #endregion
}
