// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Player.Api.Client;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Options;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using DomainEntry = Player.Vm.Api.Domain.Models.VmUsageLogEntry;
using DomainSession = Player.Vm.Api.Domain.Models.VmUsageLoggingSession;
using VmDto = Player.Vm.Api.Features.Vms.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The writer behind the usage log. Every row the report and the CSV are built from is written here, and
/// its only caller is <c>VmHub</c> - see <see cref="VmHubPresenceTests"/>, which proves the hub calls it
/// with the caller's primary teams. What is decided here is which sessions a console visit belongs to and
/// what a row says.
/// </summary>
/// <remarks>
/// <para>
/// The service is the real one over this test's own usage log database, which is a second database with
/// its own migration history - see <see cref="DatabaseFixture"/>. player.api and the local Vm store are
/// substituted, because what this class is about is the row, not where its two facts came from.
/// </para>
/// <para>
/// A session here is a <c>VmUsageLoggingSession</c>: an administrator-declared window over a set of teams,
/// managed through the endpoints <see cref="VmUsageLoggingSessionEndpointTests"/> covers. It has nothing
/// to do with a SignalR connection or with <c>TestDatabaseSession</c>.
/// </para>
/// </remarks>
public class VmUsageLoggingServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>
    /// A fixed, whole-second instant, for the same reason
    /// <see cref="VmUsageLoggingSessionEndpointTests"/> has one: PostgreSQL keeps microseconds where
    /// <see cref="DateTimeOffset"/> keeps ticks, so a value off the clock does not round trip exactly.
    /// </summary>
    private static readonly DateTimeOffset Noon = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly IPlayerService _player = Substitute.For<IPlayerService>();
    private readonly IVmService _vms = Substitute.For<IVmService>();
    private readonly List<VmLoggingContext> _contexts = [];

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();

    /// <summary>This test's usage log database, for seeding and for reading back.</summary>
    private VmLoggingContext LoggingDb { get; set; }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        LoggingDb = NewLoggingContext();

        // Every write needs a user, and only the tests about what a row says care which one.
        _player.GetUserById(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new User { Id = call.Arg<Guid>(), Name = "user" });
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        if (LoggingDb is not null)
        {
            await LoggingDb.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    #region Which sessions a visit is logged against

    [Fact]
    public async Task Create_WithNoSessions_WritesNothing()
    {
        await Service().CreateVmLogEntry(_userId, Guid.NewGuid(), [_teamId], Ct);

        Assert.Empty(await Entries());
    }

    [Fact]
    public async Task Create_WritesOneEntryPerMatchingSession()
    {
        var first = await SeedSession(name: "first");
        var second = await SeedSession(name: "second");

        await Service().CreateVmLogEntry(_userId, SeededVm(), [_teamId], Ct);

        var sessionIds = (await Entries()).Select(x => x.SessionId).ToArray();
        Assert.Equal(2, sessionIds.Length);
        Assert.Contains(first.Id, sessionIds);
        Assert.Contains(second.Id, sessionIds);
    }

    /// <summary>
    /// One shared team is enough, in either direction: the session lists the teams it covers, the caller
    /// arrives with the teams they opened the console from, and the visit is logged if the two intersect
    /// at all.
    /// </summary>
    [Fact]
    public async Task Create_MatchesASessionSharingAnyOneTeam()
    {
        var shared = Guid.NewGuid();
        await SeedSession(teamIds: [Guid.NewGuid(), shared]);

        await Service().CreateVmLogEntry(_userId, SeededVm(), [shared, Guid.NewGuid()], Ct);

        Assert.Single(await Entries());
    }

    [Fact]
    public async Task Create_SkipsASessionWithNoTeamInCommon()
    {
        await SeedSession(teamIds: [Guid.NewGuid()]);

        await Service().CreateVmLogEntry(_userId, SeededVm(), [_teamId], Ct);

        Assert.Empty(await Entries());
    }

    /// <summary>
    /// A session covering no teams at all logs nothing, which is what a session created without them
    /// looks like - the endpoint does not require any.
    /// </summary>
    [Fact]
    public async Task Create_SkipsASessionCoveringNoTeams()
    {
        await SeedSession(teamIds: []);

        await Service().CreateVmLogEntry(_userId, SeededVm(), [_teamId], Ct);

        Assert.Empty(await Entries());
    }

    [Fact]
    public async Task Create_SkipsASessionThatHasNotStartedYet()
    {
        await SeedSession(start: DateTimeOffset.UtcNow.AddYears(1));

        await Service().CreateVmLogEntry(_userId, SeededVm(), [_teamId], Ct);

        Assert.Empty(await Entries());
    }

    [Fact]
    public async Task Create_SkipsASessionThatHasAlreadyEnded()
    {
        await SeedSession(start: Noon, end: Noon.AddHours(1));

        await Service().CreateVmLogEntry(_userId, SeededVm(), [_teamId], Ct);

        Assert.Empty(await Entries());
    }

    /// <summary>
    /// An unended session has <see cref="DateTimeOffset.MinValue"/> for its end rather than a null, which
    /// is why the window test is "at or below the minimum, or in the future" rather than a null check.
    /// </summary>
    [Fact]
    public async Task Create_IncludesASessionThatHasNotEnded()
    {
        await SeedSession(end: DateTimeOffset.MinValue);

        await Service().CreateVmLogEntry(_userId, SeededVm(), [_teamId], Ct);

        Assert.Single(await Entries());
    }

    [Fact]
    public async Task Create_IncludesASessionEndingInTheFuture()
    {
        await SeedSession(end: DateTimeOffset.UtcNow.AddYears(1));

        await Service().CreateVmLogEntry(_userId, SeededVm(), [_teamId], Ct);

        Assert.Single(await Entries());
    }

    #endregion

    #region What a row says

    [Fact]
    public async Task Create_CarriesTheVmTheUserAndTheSession()
    {
        var session = await SeedSession(name: "exercise");
        var vmId = SeededVm(name: "web-01", addresses: ["10.0.0.4"]);
        _player.GetUserById(_userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = _userId, Name = "ada" });

        await Service().CreateVmLogEntry(_userId, vmId, [_teamId], Ct);

        var entry = Assert.Single(await Entries());
        Assert.Equal(session.Id, entry.SessionId);
        Assert.Equal(vmId, entry.VmId);
        Assert.Equal("web-01", entry.VmName);
        Assert.Equal("10.0.0.4", entry.IpAddress);
        Assert.Equal(_userId, entry.UserId);
        Assert.Equal("ada", entry.UserName);
    }

    /// <summary>
    /// The addresses arrive as a list and are stored as one comma-separated string, which is the shape the
    /// CSV then has to flatten again - see
    /// <c>VmUsageLoggingSessionEndpointTests.Download_FlattensACommaSeparatedIpAddressIntoOneColumn</c>.
    /// </summary>
    [Fact]
    public async Task Create_JoinsAVmsAddressesIntoOneColumn()
    {
        await SeedSession();
        var vmId = SeededVm(addresses: ["10.0.0.4", "10.0.0.5"]);

        await Service().CreateVmLogEntry(_userId, vmId, [_teamId], Ct);

        Assert.Equal("10.0.0.4, 10.0.0.5", (Assert.Single(await Entries())).IpAddress);
    }

    /// <summary>
    /// A Vm with no addresses is logged with an empty string rather than a null, which matters because the
    /// CSV cannot render a null one at all.
    /// </summary>
    [Fact]
    public async Task Create_ForAVmWithNoAddresses_WritesAnEmptyString()
    {
        await SeedSession();
        var vmId = SeededVm(addresses: []);

        await Service().CreateVmLogEntry(_userId, vmId, [_teamId], Ct);

        Assert.Equal(string.Empty, (Assert.Single(await Entries())).IpAddress);
    }

    /// <summary>
    /// The row opens with the visit and is left open: the active stamp is now, and the inactive stamp is
    /// the minimum, which is what every reader treats as "still on the Vm".
    /// </summary>
    [Fact]
    public async Task Create_StampsTheVisitAsStartedAndStillOpen()
    {
        await SeedSession();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await Service().CreateVmLogEntry(_userId, SeededVm(), [_teamId], Ct);

        var entry = Assert.Single(await Entries());
        Assert.InRange(entry.VmActiveDT, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(DateTimeOffset.MinValue, entry.VmInactiveDT);
    }

    /// <summary>
    /// The user and the Vm are looked up once however many sessions match, which is the point of the two
    /// null checks in the loop: this is on the path of every console a user opens.
    /// </summary>
    [Fact]
    public async Task Create_LooksTheUserAndTheVmUpOnceForEverySession()
    {
        await SeedSession(name: "first");
        await SeedSession(name: "second");
        await SeedSession(name: "third");
        var vmId = SeededVm();

        await Service().CreateVmLogEntry(_userId, vmId, [_teamId], Ct);

        Assert.Equal(3, (await Entries()).Length);
        await _player.Received(1).GetUserById(_userId, Arg.Any<CancellationToken>());
        await _vms.Received(1).GetAsync(vmId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// With nothing to log, neither lookup happens at all - so a console opened outside every session
    /// costs no call to player.api.
    /// </summary>
    [Fact]
    public async Task Create_WithNoMatchingSession_LooksNothingUp()
    {
        await SeedSession(teamIds: [Guid.NewGuid()]);

        await Service().CreateVmLogEntry(_userId, Guid.NewGuid(), [_teamId], Ct);

        await _player.DidNotReceive().GetUserById(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _vms.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Closing a visit

    [Fact]
    public async Task Close_StampsTheOpenEntryWithNow()
    {
        var session = await SeedSession();
        var vmId = Guid.NewGuid();
        await SeedEntry(session, vmId: vmId);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await Service().CloseVmLogEntry(_userId, vmId, Ct);

        Assert.InRange(
            (Assert.Single(await Entries())).VmInactiveDT, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Close_LeavesAnotherUsersVisitOpen()
    {
        var session = await SeedSession();
        var vmId = Guid.NewGuid();
        var someoneElse = await SeedEntry(session, vmId: vmId, userId: Guid.NewGuid());

        await Service().CloseVmLogEntry(_userId, vmId, Ct);

        Assert.Equal(DateTimeOffset.MinValue, (await Entry(someoneElse.Id)).VmInactiveDT);
    }

    [Fact]
    public async Task Close_LeavesTheSameUsersOtherVmOpen()
    {
        var session = await SeedSession();
        var otherVm = await SeedEntry(session, vmId: Guid.NewGuid());

        await Service().CloseVmLogEntry(_userId, Guid.NewGuid(), Ct);

        Assert.Equal(DateTimeOffset.MinValue, (await Entry(otherVm.Id)).VmInactiveDT);
    }

    [Fact]
    public async Task Close_LeavesAnAlreadyClosedVisitAtTheTimeItEnded()
    {
        var session = await SeedSession();
        var vmId = Guid.NewGuid();
        var finished = await SeedEntry(session, vmId: vmId, inactiveAt: Noon);

        await Service().CloseVmLogEntry(_userId, vmId, Ct);

        Assert.Equal(Noon, (await Entry(finished.Id)).VmInactiveDT);
    }

    /// <summary>
    /// Closing does not look at the session at all, so a visit still open in a session that has since
    /// ended is closed like any other. Without that, ending a session would strand every console open in
    /// it - and a stranded entry never appears in the report, which excludes anything unended.
    /// </summary>
    [Fact]
    public async Task Close_ClosesAVisitInASessionThatHasSinceEnded()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(1));
        var vmId = Guid.NewGuid();
        await SeedEntry(session, vmId: vmId);

        await Service().CloseVmLogEntry(_userId, vmId, Ct);

        Assert.NotEqual(DateTimeOffset.MinValue, (Assert.Single(await Entries())).VmInactiveDT);
    }

    [Fact]
    public async Task Close_WithNoOpenVisit_ChangesNothing()
    {
        var session = await SeedSession();
        var closed = await SeedEntry(session, inactiveAt: Noon);

        await Service().CloseVmLogEntry(_userId, closed.VmId, Ct);

        Assert.Equal(Noon, (await Entry(closed.Id)).VmInactiveDT);
    }

    /// <summary>
    /// Opening the same Vm twice without closing it in between leaves two open rows, and closing then
    /// stamps both with the same instant - so the report counts the time twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Characterized rather than fixed, and reachable: <c>VmHub.SetActiveVirtualMachine</c> writes an
    /// entry every time a console is opened and never closes the previous one, so a reconnecting client
    /// that opens the same console again does this.
    /// </para>
    /// <para>
    /// Switching between two Vms is the other half of it and is worse: the entry for the first Vm is
    /// never closed at all, because a close only ever names the Vm being left, and the presence store has
    /// already forgotten the first one. That row stays open forever and the report drops it, so the time
    /// is lost rather than doubled. Both would be fixed in the same place - closing the previous entry
    /// when a new one is opened.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Create_TwiceForTheSameVm_LeavesTwoOpenVisitsThatBothGetClosed()
    {
        await SeedSession();
        var vmId = SeededVm();
        var service = Service();

        await service.CreateVmLogEntry(_userId, vmId, [_teamId], Ct);
        await service.CreateVmLogEntry(_userId, vmId, [_teamId], Ct);

        Assert.Equal(2, (await Entries()).Length);

        await Service().CloseVmLogEntry(_userId, vmId, Ct);

        Assert.All(
            await Entries(),
            entry => Assert.NotEqual(DateTimeOffset.MinValue, entry.VmInactiveDT));
    }

    #endregion

    #region Turning it off

    /// <summary>
    /// The option is checked in both methods, so a service constructed with logging off writes nothing and
    /// asks player.api nothing.
    /// </summary>
    /// <remarks>
    /// Belt and braces rather than the real switch: <c>Startup</c> only registers this class when the
    /// option is on, and registers <see cref="DisabledVmUsageLoggingService"/> when it is off - see
    /// <see cref="TheDisabledService_WritesNothingForAMatchingSession"/>. The guard is what makes the
    /// class safe to resolve either way.
    /// </remarks>
    [Fact]
    public async Task WithLoggingOff_WritesNothingAndLooksNothingUp()
    {
        var session = await SeedSession();
        var vmId = Guid.NewGuid();
        var open = await SeedEntry(session, vmId: vmId);
        var service = Service(enabled: false);

        await service.CreateVmLogEntry(_userId, vmId, [_teamId], Ct);
        await service.CloseVmLogEntry(_userId, vmId, Ct);

        Assert.Equal(open.Id, (Assert.Single(await Entries())).Id);
        Assert.Equal(DateTimeOffset.MinValue, (await Entry(open.Id)).VmInactiveDT);
        await _player.DidNotReceive().GetUserById(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The null object the host registers when the feature is off. It holds no database and no client, so
    /// there is nothing it could write - which is the assertion: a future version that delegated to the
    /// real writer would fail this.
    /// </summary>
    [Fact]
    public async Task TheDisabledService_WritesNothingForAMatchingSession()
    {
        var session = await SeedSession();
        var vmId = Guid.NewGuid();
        var open = await SeedEntry(session, vmId: vmId);
        IVmUsageLoggingService service = new DisabledVmUsageLoggingService();

        await service.CreateVmLogEntry(_userId, vmId, [_teamId], Ct);
        await service.CloseVmLogEntry(_userId, vmId, Ct);

        Assert.Equal(open.Id, (Assert.Single(await Entries())).Id);
        Assert.Equal(DateTimeOffset.MinValue, (await Entry(open.Id)).VmInactiveDT);
    }

    #endregion

    #region Arrangement

    /// <summary>
    /// The service under test, over a usage log context of its own so that what a test reads back has been
    /// through the database rather than out of the same change tracker.
    /// </summary>
    private VmUsageLoggingService Service(bool enabled = true)
    {
        var context = NewLoggingContext();
        _contexts.Add(context);

        return new VmUsageLoggingService(
            new VmUsageLoggingOptions { Enabled = enabled }, _player, _vms, context);
    }

    /// <summary>An open session over <see cref="_teamId"/>, started an hour ago.</summary>
    private async Task<DomainSession> SeedSession(
        Guid[] teamIds = null,
        string name = "session",
        DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        var session = new DomainSession
        {
            ViewId = Guid.NewGuid(),
            TeamIds = teamIds ?? [_teamId],
            SessionName = name,
            CreatedDt = Noon,
            SessionStart = start ?? DateTimeOffset.UtcNow.AddHours(-1),
            SessionEnd = end ?? DateTimeOffset.MinValue,
        };

        LoggingDb.Add(session);
        await LoggingDb.SaveChangesAsync(Ct);

        return session;
    }

    /// <summary>An entry for <see cref="_userId"/>, open unless given a time it ended.</summary>
    private async Task<DomainEntry> SeedEntry(
        DomainSession session,
        Guid? vmId = null,
        Guid? userId = null,
        DateTimeOffset? inactiveAt = null)
    {
        var entry = new DomainEntry
        {
            SessionId = session.Id,
            VmId = vmId ?? Guid.NewGuid(),
            VmName = "vm",
            IpAddress = "10.0.0.1",
            UserId = userId ?? _userId,
            UserName = "user",
            VmActiveDT = Noon,
            VmInactiveDT = inactiveAt ?? DateTimeOffset.MinValue,
        };

        LoggingDb.Add(entry);
        await LoggingDb.SaveChangesAsync(Ct);

        return entry;
    }

    /// <summary>
    /// A Vm as the local store answers for it. Returns its id, which is all a caller of the writer has.
    /// </summary>
    private Guid SeededVm(string name = "vm", string[] addresses = null)
    {
        var vm = new VmDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            IpAddresses = addresses ?? ["10.0.0.1"],
        };

        _vms.GetAsync(vm.Id, Arg.Any<CancellationToken>()).Returns(vm);

        return vm.Id;
    }

    private async Task<DomainEntry[]> Entries()
    {
        await using var context = NewLoggingContext();

        return await context.VmUsageLogEntries.ToArrayAsync(Ct);
    }

    private async Task<DomainEntry> Entry(Guid id)
    {
        await using var context = NewLoggingContext();

        return await context.VmUsageLogEntries.SingleAsync(x => x.Id == id, Ct);
    }

    #endregion
}
