// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Api.Client;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Options;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using PlayerTeam = Player.Api.Client.Team;

namespace Player.Vm.Api.Tests;

/// <summary>
/// What actually happens after <see cref="CallbacksEndpointTests"/>'s 202. A view created from a template
/// gets copies of the parent's maps and usage logging session; a view deleted takes its maps with it and has
/// its sessions closed. This is the only part of the application that does work for a caller who is already
/// gone, and the only part every other test substitutes away.
/// </summary>
/// <remarks>
/// <para>
/// The work happens on an <c>ActionBlock</c> the constructor builds, which is why it is substituted
/// everywhere else: nothing a request can await tells you it is finished. So these tests hand an event to
/// the real queue and then wait, bounded, for the effect - see <see cref="Eventually"/>. The one thing worth
/// remembering about that queue is that it is in memory, so what makes an event survive a restart is the row
/// the endpoint stored, and <see cref="Startup_ProcessesEveryEventAlreadyStored"/> is the test that the row
/// is picked up again.
/// </para>
/// <para>
/// Real database, real player.api client over a substituted transport - see <see cref="TestHttpHandler"/> -
/// and a scope factory over this test's own session, because the service resolves a context per event rather
/// than holding one. Payloads are written as JSON rather than serialized from the client's DTOs: player.api
/// sends the payload as a string, the service parses it with Newtonsoft while the DTOs are annotated for
/// System.Text.Json, and what makes that work is only that the names match case-insensitively. A test that
/// serialized with Newtonsoft too would not be holding that still.
/// </para>
/// </remarks>
public class CallbackBackgroundServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly TestHttpHandler _http = new();
    private readonly List<CallbackBackgroundService> _services = [];

    public override async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        await base.DisposeAsync();
    }

    #region A view created from a parent

    /// <summary>
    /// The clone, and the rule that decides who can see it: teams are matched by <em>name</em>, because the
    /// child view's teams are new rows with new ids. A map placed on the parent's "Red Team" ends up on the
    /// child's "Red Team", and the coordinates - the clickable Vms on the map - come with it.
    /// </summary>
    [Fact]
    public async Task ViewCreated_ClonesTheParentsMapsOntoTheChildTeamsOfTheSameName()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        var (parentRed, childRed, childBlue) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedMap(parentView, "Overview", [parentRed]);
        Teams(parentView, (parentRed, "Red Team"));
        Teams(childView, (childRed, "Red Team"), (childBlue, "Blue Team"));

        await Process(Created(childView, parentView));

        var clone = await Eventually(
            async context => await context.Maps
                .Include(x => x.Coordinates)
                .SingleOrDefaultAsync(x => x.ViewId == childView, Ct),
            "the parent's map to be cloned onto the new view");

        Assert.Equal("Overview", clone.Name);
        Assert.Equal<Guid>([childRed], clone.TeamIds);
        Assert.Equal("vm-1", Assert.Single(clone.Coordinates).Label);
    }

    /// <summary>
    /// The parent keeps its own map, with its own teams and its own coordinate rows: this is a copy, not a
    /// move. Worth its own assertion because <c>VmMap.Clone</c> is a <c>MemberwiseClone</c>, so a coordinate
    /// list shared by reference rather than cloned would move the parent's coordinates onto the child.
    /// </summary>
    [Fact]
    public async Task ViewCreated_LeavesTheParentsMapAsItWas()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        var (parentRed, childRed) = (Guid.NewGuid(), Guid.NewGuid());
        var map = await SeedMap(parentView, "Overview", [parentRed]);
        Teams(parentView, (parentRed, "Red Team"));
        Teams(childView, (childRed, "Red Team"));

        await Process(Created(childView, parentView));

        await Eventually(
            async context => await context.Maps.CountAsync(Ct) == 2 ? "" : null,
            "both maps to exist");

        await using var reader = NewContext();
        var parent = await reader.Maps.Include(x => x.Coordinates).SingleAsync(x => x.Id == map.Id, Ct);
        Assert.Equal<Guid>([parentRed], parent.TeamIds);
        Assert.Single(parent.Coordinates);
    }

    /// <summary>
    /// A view created from nothing - the ordinary case, since most views are not clones - is discarded
    /// without a single call to player.api. The <c>ParentId</c> check is the gate, and it is what keeps the
    /// hundreds of ordinary view creations out of this code entirely.
    /// </summary>
    [Fact]
    public async Task ViewCreated_WithNoParent_AsksPlayerNothing()
    {
        var childView = Guid.NewGuid();
        await SeedMap(Guid.NewGuid(), "Overview", [Guid.NewGuid()]);

        await Process(Created(childView, parentId: null));

        await Handled();
        Assert.Empty(_http.Sent);
    }

    /// <summary>
    /// A parent with no maps is the same: nothing to clone, so nothing is asked. The early return matters
    /// because the alternative is two player.api calls per view creation in a deployment that does not use
    /// maps at all.
    /// </summary>
    [Fact]
    public async Task ViewCreated_WhenTheParentHasNoMaps_AsksPlayerNothing()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedMap(Guid.NewGuid(), "Someone else's map", [Guid.NewGuid()]);

        await Process(Created(childView, parentView));

        await Handled();
        Assert.Empty(_http.Sent);
    }

    /// <summary>
    /// A map on a parent team the child has no namesake for is still cloned, with no teams on it. The map
    /// exists, an administrator can see it and assign teams to it, and no member sees a map meant for someone
    /// else - which is the safe way round for the name matching to fail.
    /// </summary>
    [Fact]
    public async Task ViewCreated_ForATeamWithNoNamesakeInTheChild_ClonesTheMapWithNoTeams()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        var parentRed = Guid.NewGuid();
        await SeedMap(parentView, "Overview", [parentRed]);
        Teams(parentView, (parentRed, "Red Team"));
        Teams(childView, (Guid.NewGuid(), "Renamed Team"));

        await Process(Created(childView, parentView));

        var clone = await Eventually(
            async context => await context.Maps.SingleOrDefaultAsync(x => x.ViewId == childView, Ct),
            "the map to be cloned");

        Assert.Empty(clone.TeamIds);
    }

    /// <summary>
    /// A view deleted between the callback being sent and this event being processed - which the retry makes
    /// entirely possible - clones nothing rather than cloning maps nobody can reach. 404 is the one status
    /// the clone path recognizes.
    /// </summary>
    [Fact]
    public async Task ViewCreated_WhenPlayerNoLongerHasTheView_ClonesNothing()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedMap(parentView, "Overview", [Guid.NewGuid()]);
        _http.Answers($"api/views/{parentView}/teams", HttpStatusCode.NotFound);

        await Process(Created(childView, parentView));

        await Handled();

        await using var reader = NewContext();
        Assert.Single(await reader.Maps.ToListAsync(Ct));
    }

    /// <summary>
    /// Any other failure from player.api produces the maps with no teams at all, and the event is then
    /// discarded as successfully processed - so nobody sees the clones and nothing retries.
    /// </summary>
    /// <remarks>
    /// Characterized, and the sharpest thing in this file. The <c>catch (ApiException)</c> in
    /// <c>CloneMaps</c> returns for a 404 and swallows everything else, leaving both team sets empty and
    /// execution carrying on into the loop that builds the clones. A 502 from a restarting player.api
    /// therefore does not become a retry, which the surrounding machinery is built for and which would fix
    /// it; it becomes a set of maps an administrator has to reassign by hand, indistinguishable from
    /// <see cref="ViewCreated_ForATeamWithNoNamesakeInTheChild_ClonesTheMapWithNoTeams"/>. Rethrowing
    /// anything that is not a 404 would be a one-line change; it is left alone here because a test that
    /// documents the behavior is a prerequisite for changing it, not a decision to keep it.
    /// </remarks>
    [Fact]
    public async Task ViewCreated_WhenPlayerFails_ClonesTheMapsWithNoTeamsAndDiscardsTheEvent()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        var parentRed = Guid.NewGuid();
        await SeedMap(parentView, "Overview", [parentRed]);
        _http.Answers($"api/views/{parentView}/teams", HttpStatusCode.BadGateway);

        await Process(Created(childView, parentView));

        var clone = await Eventually(
            async context => await context.Maps.SingleOrDefaultAsync(x => x.ViewId == childView, Ct),
            "the map to be cloned in spite of the failure");

        Assert.Empty(clone.TeamIds);
        await Handled();
    }

    /// <summary>
    /// The usage log's half of the clone: a parent with a logging session gives the child one of its own,
    /// named after the new view, covering every team of it and running for a year. Unlike the maps, the child
    /// session is not matched by team name - a new view's session covers all of its teams.
    /// </summary>
    [Fact]
    public async Task ViewCreated_ClonesTheParentsUsageLoggingSession()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedSession(parentView);
        Teams(childView, (first, "Red Team"), (second, "Blue Team"));

        await Process(Created(childView, parentView, name: "Cloned View"));

        await using var logging = NewLoggingContext();
        var clone = await Eventually(
            async () => await logging.VmUsageLoggingSessions
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.ViewId == childView, Ct),
            "the parent's logging session to be cloned");

        Assert.Equal("Cloned View", clone.SessionName);
        Assert.Equal<Guid>([first, second], clone.TeamIds);
        Assert.True(clone.SessionEnd > clone.SessionStart);
    }

    /// <summary>
    /// A deployment with usage logging switched off has no <c>VmLoggingContext</c> registered at all, so the
    /// service resolves null for it and has to carry on. The maps are the part that must still be cloned.
    /// </summary>
    /// <remarks>
    /// The null check is in <c>CloneVmLoggingSessions</c> and <c>EndVmLoggingSessions</c> rather than at the
    /// call site, so this covers a guard that exists twice - see
    /// <see cref="ViewDeleted_WithoutAUsageLogDatabase_StillDeletesTheMaps"/> for the other.
    /// <c>VmUsageLoggingDisabledEndpointTests</c> is the same deployment seen from the outside.
    /// </remarks>
    [Fact]
    public async Task ViewCreated_WithoutAUsageLogDatabase_StillClonesTheMaps()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        var (parentRed, childRed) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedMap(parentView, "Overview", [parentRed]);
        Teams(parentView, (parentRed, "Red Team"));
        Teams(childView, (childRed, "Red Team"));

        await Process(Created(childView, parentView), usageLogging: false);

        var clone = await Eventually(
            async context => await context.Maps.SingleOrDefaultAsync(x => x.ViewId == childView, Ct),
            "the map to be cloned without a usage log database");

        Assert.Equal<Guid>([childRed], clone.TeamIds);
    }

    #endregion

    #region A view deleted

    /// <summary>
    /// The view's maps go, with their coordinates - and nobody else's do. The coordinates are removed
    /// explicitly rather than left to a cascade, so a map deleted without them would leave orphaned rows.
    /// </summary>
    [Fact]
    public async Task ViewDeleted_DeletesTheViewsMapsAndTheirCoordinates()
    {
        var (deleted, other) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedMap(deleted, "Going", [Guid.NewGuid()]);
        var survivor = await SeedMap(other, "Staying", [Guid.NewGuid()]);

        await Process(Deleted(deleted));

        await Eventually(
            async context => await context.Maps.CountAsync(Ct) == 1 ? "" : null,
            "the deleted view's map to be removed");

        await using var reader = NewContext();
        Assert.Equal(survivor.Id, (await reader.Maps.SingleAsync(Ct)).Id);
        Assert.Single(await reader.Set<Coordinate>().ToListAsync(Ct));
    }

    /// <summary>
    /// A session still running when its view is deleted is ended now, so the report and the CSV stop at the
    /// point the view stopped existing rather than at the year-long end date the clone gave it.
    /// </summary>
    [Fact]
    public async Task ViewDeleted_EndsTheViewsRunningSessions()
    {
        var viewId = Guid.NewGuid();
        var session = await SeedSession(viewId, end: DateTimeOffset.UtcNow.AddYears(1));

        await Process(Deleted(viewId));

        await using var logging = NewLoggingContext();
        var ended = await Eventually(
            async () => await logging.VmUsageLoggingSessions
                .AsNoTracking()
                .Where(x => x.Id == session.Id && x.SessionEnd < DateTimeOffset.UtcNow)
                .SingleOrDefaultAsync(Ct),
            "the running session to be ended");

        Assert.True(ended.SessionEnd > ended.SessionStart);
    }

    /// <summary>
    /// A session that had already finished keeps the time it finished at. Overwriting it would move
    /// yesterday's exercise to the day its view was tidied up, and the usage report is a record of what
    /// happened.
    /// </summary>
    [Fact]
    public async Task ViewDeleted_ForASessionThatAlreadyEnded_LeavesItsEndTime()
    {
        var viewId = Guid.NewGuid();
        var ended = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);
        var session = await SeedSession(viewId, end: ended);

        await Process(Deleted(viewId));

        await Handled();

        await using var logging = NewLoggingContext();
        Assert.Equal(
            ended,
            (await logging.VmUsageLoggingSessions.SingleAsync(x => x.Id == session.Id, Ct)).SessionEnd);
    }

    /// <summary>The other half of the disabled-usage-logging guard, on the delete side.</summary>
    [Fact]
    public async Task ViewDeleted_WithoutAUsageLogDatabase_StillDeletesTheMaps()
    {
        var viewId = Guid.NewGuid();
        await SeedMap(viewId, "Going", [Guid.NewGuid()]);

        await Process(Deleted(viewId), usageLogging: false);

        await Eventually(
            async context => await context.Maps.AnyAsync(Ct) ? null : "",
            "the map to be removed without a usage log database");
    }

    #endregion

    #region The event itself

    /// <summary>
    /// A view created and then deleted leaves nothing behind, and the two events are processed in the order
    /// they were handed over: the clone is made and then removed. One queue, one at a time.
    /// </summary>
    [Fact]
    public async Task TwoEvents_AreProcessedInOrder()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        var (parentRed, childRed) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedMap(parentView, "Overview", [parentRed]);
        Teams(parentView, (parentRed, "Red Team"));
        Teams(childView, (childRed, "Red Team"));

        var (create, delete) = (Created(childView, parentView), Deleted(childView));
        await Seed(create, delete);

        var service = Service();
        await service.AddEvent(create);
        await service.AddEvent(delete);

        await Handled();

        // One map left, and it is the parent's: the clone was made and then deleted. Had the delete been
        // processed first there would be two, because the clone did not exist yet for it to remove.
        await using var reader = NewContext();
        Assert.Equal(parentView, (await reader.Maps.SingleAsync(Ct)).ViewId);
    }

    /// <summary>
    /// The queue is in memory, so a restart with events still stored has to pick them up - which is the whole
    /// reason <see cref="CallbacksEndpointTests"/>'s endpoint stores before it acknowledges. Starting the
    /// service is what drains them.
    /// </summary>
    [Fact]
    public async Task Startup_ProcessesEveryEventAlreadyStored()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedMap(first, "First", [Guid.NewGuid()]);
        await SeedMap(second, "Second", [Guid.NewGuid()]);
        await Seed(Deleted(first), Deleted(second));

        var service = Service();
        await service.StartAsync(Ct);

        await Eventually(
            async context => await context.Maps.AnyAsync(Ct) ? null : "",
            "both stored events to be processed");

        await using var reader = NewContext();
        Assert.Empty(await reader.WebhookEvents.ToListAsync(Ct));
    }

    /// <summary>
    /// An event that fails is kept and tried again. player.api being unreachable is the case this exists for:
    /// the clone would otherwise be lost, and the view it was for lives for weeks.
    /// </summary>
    /// <remarks>
    /// The slow test in this file, and deliberately so - the first retry is five seconds after the failure,
    /// from constants in <c>WebhookEventWrapper</c> with no seam to shorten them, and a retry that only
    /// happens in production is not a retry anyone can rely on. The failure is a request nothing stubbed,
    /// which <see cref="TestHttpHandler"/> raises as an exception rather than a status, because that is the
    /// only shape of failure the clone path does not swallow -
    /// <see cref="ViewCreated_WhenPlayerFails_ClonesTheMapsWithNoTeamsAndDiscardsTheEvent"/> is what it does
    /// with the other. What is not covered is the expiry that goes with it: an event is abandoned once it is
    /// 48 hours old, and only on a retry.
    /// </remarks>
    [Fact]
    public async Task WhenTheFirstAttemptFails_TheEventIsKeptAndRetried()
    {
        var (parentView, childView) = (Guid.NewGuid(), Guid.NewGuid());
        var parentRed = Guid.NewGuid();
        await SeedMap(parentView, "Overview", [parentRed]);

        await Process(Created(childView, parentView));

        // The attempt has been made and has failed: the request went out and nothing answered it.
        await Eventually(() => Task.FromResult(_http.Sent.Count > 0 ? "" : null), "the first attempt to fail");

        // So the event is still there, and the map has not been cloned.
        await using (var reader = NewContext())
        {
            Assert.Single(await reader.WebhookEvents.ToListAsync(Ct));
            Assert.Single(await reader.Maps.ToListAsync(Ct));
        }

        // Now let the retry find player.api answering, five seconds later.
        Teams(parentView, (parentRed, "Red Team"));
        Teams(childView, (Guid.NewGuid(), "Red Team"));

        await Eventually(
            async context => await context.Maps.CountAsync(Ct) == 2 ? "" : null,
            "the retried event to clone the map",
            seconds: 20);

        await Handled();
    }

    #endregion

    #region Arrangement

    /// <summary>
    /// The service under test, and the events handed to its real queue. Held for disposal, which is what
    /// stops the <c>ActionBlock</c> outliving the test.
    /// </summary>
    private CallbackBackgroundService Service(bool usageLogging = true)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_http, disposeHandler: false));

        var options = Substitute.For<IOptionsMonitor<ClientOptions>>();
        options.CurrentValue.Returns(
            new ClientOptions { urls = new ApiUrlSettings { playerApi = "https://player.test.local/" } });

        var service = new CallbackBackgroundService(
            new TestScopes(NewContext, usageLogging ? NewLoggingContext : null),
            TestMapper.Value,
            Substitute.For<ILogger<CallbackBackgroundService>>(),
            factory,
            options);

        _services.Add(service);

        return service;
    }

    /// <summary>
    /// Stores an event and hands it to the queue, which is what the callback endpoint does and in that
    /// order.
    /// </summary>
    /// <remarks>
    /// The row is not optional. The service removes the event by identity once it has processed it, so an
    /// event that was never stored fails its save with a concurrency exception and goes down the retry path -
    /// which would make every test here a test of the retry.
    /// </remarks>
    private async Task Process(WebhookEvent e, bool usageLogging = true)
    {
        await Seed(e);
        await Service(usageLogging).AddEvent(e);
    }

    /// <summary>
    /// A view creation as player.api sends it: the payload is a JSON string on the event, and the event
    /// carries an id because the endpoint saved it before queueing it.
    /// </summary>
    private static WebhookEvent Created(Guid viewId, Guid? parentId, string name = "Child View") =>
        new()
        {
            Id = Guid.NewGuid(),
            Type = EventType.ViewCreated,
            Timestamp = DateTimeOffset.UtcNow,
            Payload =
                $"{{\"viewId\":\"{viewId}\",\"viewName\":\"{name}\"," +
                $"\"parentId\":{(parentId.HasValue ? $"\"{parentId}\"" : "null")}}}",
        };

    private static WebhookEvent Deleted(Guid viewId) =>
        new()
        {
            Id = Guid.NewGuid(),
            Type = EventType.ViewDeleted,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = $"{{\"viewId\":\"{viewId}\"}}",
        };

    /// <summary>What player.api answers for a view's teams, which is all the clone path asks it.</summary>
    private void Teams(Guid viewId, params (Guid Id, string Name)[] teams) =>
        _http.Answers(
            $"api/views/{viewId}/teams",
            teams.Select(x => new PlayerTeam { Id = x.Id, Name = x.Name, ViewId = viewId }).ToArray());

    /// <summary>A map on one view, with a single coordinate so that the clone has something to copy.</summary>
    private async Task<VmMap> SeedMap(Guid viewId, string name, List<Guid> teamIds)
    {
        var map = new VmMap
        {
            ViewId = viewId,
            Name = name,
            ImageUrl = "https://maps.test.local/image.png",
            TeamIds = teamIds,
            Coordinates =
            [
                new Coordinate
                {
                    Label = "vm-1",
                    Urls = ["https://console.test.local/vm-1"],
                    XPosition = 1,
                    YPosition = 2,
                    Radius = 3,
                },
            ],
        };

        await Seed(map);

        return map;
    }

    /// <summary>A usage logging session on one view, in this test's usage log database.</summary>
    private async Task<VmUsageLoggingSession> SeedSession(Guid viewId, DateTimeOffset? end = null)
    {
        await using var logging = NewLoggingContext();

        var session = new VmUsageLoggingSession
        {
            ViewId = viewId,
            TeamIds = [Guid.NewGuid()],
            SessionName = "Parent View",
            CreatedDt = DateTimeOffset.UtcNow,
            SessionStart = DateTimeOffset.UtcNow.AddHours(-1),
            SessionEnd = end ?? DateTimeOffset.UtcNow.AddYears(1),
        };

        logging.Add(session);
        await logging.SaveChangesAsync(Ct);

        return session;
    }

    /// <summary>
    /// Waits, bounded, for something the queue does on a thread of its own. A missing effect has to fail
    /// rather than hang, which is the same reason the SignalR tests wait with a timeout.
    /// </summary>
    private static async Task<T> Eventually<T>(Func<Task<T>> read, string what, int seconds = 10)
        where T : class
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var value = await read();

            if (value is not null)
            {
                return value;
            }

            if (clock.Elapsed.TotalSeconds > seconds)
            {
                Assert.Fail($"Waited {seconds}s for {what} and it did not happen.");
            }

            await Task.Delay(100, Ct);
        }
    }

    /// <summary>
    /// The same wait, reading through a context of its own each time: an effect on another thread is not
    /// visible through a change tracker that has already loaded the rows.
    /// </summary>
    private async Task<T> Eventually<T>(Func<VmContext, Task<T>> read, string what, int seconds = 10)
        where T : class =>
        await Eventually(
            async () =>
            {
                await using var context = NewContext();

                return await read(context);
            },
            what,
            seconds);

    /// <summary>
    /// Waits for the queue to have finished with whatever it was given, for the assertions that are about
    /// something <em>not</em> happening. The event row being gone is the service's own report that it is
    /// done, and it is removed after the work rather than before.
    /// </summary>
    private async Task Handled() =>
        await Eventually(
            async context => await context.WebhookEvents.AnyAsync(Ct) ? null : "",
            "the event to be processed and removed");

    /// <summary>
    /// The scope factory the service resolves its contexts from - a context per event, as the production
    /// registration gives it, over this test's own databases.
    /// </summary>
    /// <remarks>
    /// Written out rather than substituted because the scope owns what it resolves: a substituted scope
    /// disposes nothing, and a context per event left open would hold a pooled connection for the rest of the
    /// run. A null <paramref name="logging"/> is a deployment with usage logging switched off, where the
    /// service resolves null for the context.
    /// </remarks>
    private sealed class TestScopes(Func<VmContext> vm, Func<VmLoggingContext> logging) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(vm(), logging?.Invoke());

        private sealed class Scope(VmContext vm, VmLoggingContext logging) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object GetService(Type serviceType)
            {
                if (serviceType == typeof(VmContext))
                {
                    return vm;
                }

                return serviceType == typeof(VmLoggingContext) ? logging : null;
            }

            public void Dispose()
            {
                vm?.Dispose();
                logging?.Dispose();
            }
        }
    }

    #endregion
}
