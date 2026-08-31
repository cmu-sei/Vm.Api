// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Tests.Infrastructure;
using VimClient;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// <c>MachineStateService</c>, the poller that makes the VM UI's power indicator follow a machine
/// somebody powered on or off outside Player: it asks each vCenter for the power events raised since it
/// last looked, resolves each vCenter moref to a Player Vm and writes <c>Vm.PowerState</c> from the
/// newest event per machine. Nothing else in the application does that - a power command sent through
/// the API is written by the command handler, and everything a hypervisor does on its own arrives here
/// or not at all.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="PollLoop"/>, which is what makes a pass of a background loop countable, and
/// against a substituted <c>IVsphereService</c> and <c>IConnectionService</c>, so no vCenter is involved.
/// The poll interval is always configured as a minute (<see cref="NeverOnItsOwn"/>) so that only the
/// harness's own nudge - <c>CheckState</c> - advances the loop, and a test is an assertion rather than a
/// race. The one exception is
/// <see cref="TheIntervalItWaitsIsTheTaskPollersOwnSetting_NotOneOfItsOwn"/>, whose subject is the
/// interval.
/// </para>
/// <para>
/// What this class deliberately does not restate: the save at the end of a pass raises an entity event
/// like any other, so the clients hear about the new state through <c>VmUpdatedSignalRHandler</c> -
/// which is <c>EntityEventBroadcastTests</c>' and <c>VmSignalRHandlerTests</c>' subject, not this one's.
/// Everything below stops at the row.
/// </para>
/// <para>
/// Two things about the write path are worth knowing and are asserted nowhere, because there is no seam
/// on the context to assert them through. The query at <c>MachineStateService.cs:183</c> carries an
/// <c>Include(x =&gt; x.VmTeams)</c> that nothing in the method reads, so every pass that has any event
/// at all loads the team rows of every machine it is about to touch. And neither that query nor the
/// <c>SaveChangesAsync()</c> at <c>:206</c> is given a cancellation token, so a pass in flight when the
/// host shuts down finishes its write rather than being cancelled.
/// </para>
/// </remarks>
public class MachineStateServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>
    /// A minute, so nothing but the harness's nudge can turn the loop. See <see cref="PollLoop"/>.
    /// </summary>
    private const int NeverOnItsOwn = 60_000;

    /// <summary>
    /// Long enough that "when the pass started" and "when the pass finished" are far apart on the clock,
    /// which is the only way the window arithmetic between two passes can be told apart.
    /// </summary>
    private const int SlowPass = 150;

    private const string VcenterA = "vcenter-a.example.test";
    private const string VcenterB = "vcenter-b.example.test";

    // vCenter morefs. Only unique within one vCenter, which is the whole reason GetVmIdByRef takes a host.
    private const string MorefOne = "vm-101";
    private const string MorefTwo = "vm-202";

    private static readonly Guid VmA = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid VmB = new("bbbbbbbb-0000-4000-8000-000000000002");

    // Event timestamps. Unrelated to the filter window - the service asks vCenter to apply that, and
    // only ever compares createdTime values against each other.
    private static readonly DateTime Earlier = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Earlier.AddMinutes(5);

    #region Which state an event means

    /// <summary>
    /// The three event types the poller subscribes to and the state each one writes. The DRS row is the
    /// one worth the theory: a power-on that DRS initiated is a different class from an ordinary one, so
    /// a mapping written for <c>VmPoweredOnEvent</c> alone would leave a machine that vCenter itself
    /// started showing as off until somebody touched it through Player.
    /// </summary>
    [Theory]
    [InlineData(typeof(VmPoweredOnEvent), PowerState.On)]
    [InlineData(typeof(DrsVmPoweredOnEvent), PowerState.On)]
    [InlineData(typeof(VmPoweredOffEvent), PowerState.Off)]
    public async Task EachPowerEventVcenterRaises_BecomesTheStateTheIndicatorShows(
        Type eventType, PowerState expected)
    {
        var poller = NewPoller();
        var vcenter = poller.Vcenter(VcenterA);
        poller.Reports(vcenter, Evt(eventType, MorefOne, Earlier));
        poller.Resolves(MorefOne, vcenter, VmA);
        await Seed(new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.Unknown });

        await poller.Run();

        Assert.Equal(expected, await StateOf(VmA));
    }

    /// <summary>
    /// A batch holding both a power-off and a power-on for one machine ends at the newer of the two,
    /// whichever order vCenter listed them in. This is what stops a machine that was bounced between two
    /// polls - or a batch that arrived out of order - from leaving the indicator on the wrong state until
    /// something else happens to that machine.
    /// </summary>
    /// <remarks>
    /// Driven in both list orders on purpose. With the events already newest-first, the ordering the
    /// grouping at <c>MachineStateService.cs:169</c> applies could be deleted entirely and the test would
    /// still pass on the accident of the input.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForOneMachine_TheNewestEventInTheBatchWins(bool newestFirst)
    {
        var poller = NewPoller();
        var vcenter = poller.Vcenter(VcenterA);
        Event[] events = newestFirst
            ? [PoweredOn(MorefOne, Later), PoweredOff(MorefOne, Earlier)]
            : [PoweredOff(MorefOne, Earlier), PoweredOn(MorefOne, Later)];
        poller.Reports(vcenter, events);
        poller.Resolves(MorefOne, vcenter, VmA);
        await Seed(new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.Off });

        await poller.Run();

        Assert.Equal(PowerState.On, await StateOf(VmA));
    }

    /// <summary>
    /// The newest-event rule is applied per vCenter and not across them: where two vCenters both report
    /// an event that resolves to the same Player Vm, the first connection in the list wins outright and
    /// the other's event is discarded however much newer it is.
    /// </summary>
    /// <remarks>
    /// The grouping and ordering at <c>MachineStateService.cs:168-170</c> run once per connection, and the
    /// cross-connection merge is <c>eventDict.TryAdd</c> at <c>:178</c> - first writer keeps the slot.
    /// Reaching it needs one machine resolvable from two vCenters, which is what a machine moved between
    /// them looks like while both connection caches still hold its moref, so it is narrow rather than
    /// impossible; changing <c>TryAdd</c> to an assignment would make the newest win here as it does
    /// within a connection. The order the connections are examined in is
    /// <c>IConnectionService.GetAllConnections</c>' order, so which one wins is decided by
    /// configuration.
    /// </remarks>
    [Fact]
    public async Task WhenTwoVcentersBothNameTheSameVm_TheFirstConnectionWinsRatherThanTheNewerEvent()
    {
        var poller = NewPoller();
        var first = poller.Vcenter(VcenterA);
        var second = poller.Vcenter(VcenterB);
        poller.Reports(first, PoweredOff(MorefOne, Earlier));
        poller.Reports(second, PoweredOn(MorefOne, Later));
        poller.Resolves(MorefOne, first, VmA);
        poller.Resolves(MorefOne, second, VmA);
        await Seed(new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.Unknown });

        await poller.Run();

        Assert.Equal(PowerState.Off, await StateOf(VmA));
    }

    #endregion

    #region Which vCenters are asked

    // A vCenter configured with Enabled false is not dialed at all. Nothing else refuses it: the whole of
    // that switch for this poller is the guard at MachineStateService.cs:100, and an install that has
    // turned a vCenter off has usually turned it off because it is unreachable.
    [Fact]
    public async Task ADisabledConnection_IsNotAskedForEventsAtAll()
    {
        var poller = NewPoller();
        var enabled = poller.Vcenter(VcenterA);
        var disabled = poller.Vcenter(VcenterB, enabled: false);
        poller.Reports(enabled, PoweredOn(MorefOne, Earlier));
        poller.Resolves(MorefOne, enabled, VmA);
        await Seed(new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.Off });

        await poller.Run();

        Assert.Empty(poller.AskedOf(disabled));
        Assert.Single(poller.AskedOf(enabled));
        Assert.Equal(PowerState.On, await StateOf(VmA));
    }

    // Both enabled vCenters are asked in one pass and both answers are applied to the same save, so an
    // installation with more than one vCenter does not get one of them updated per poll.
    [Fact]
    public async Task TwoEnabledConnections_AreBothAskedAndBothAnswersAreApplied()
    {
        var poller = NewPoller();
        var first = poller.Vcenter(VcenterA);
        var second = poller.Vcenter(VcenterB);
        poller.Reports(first, PoweredOn(MorefOne, Earlier));
        poller.Reports(second, PoweredOff(MorefTwo, Earlier));
        poller.Resolves(MorefOne, first, VmA);
        poller.Resolves(MorefTwo, second, VmB);
        await Seed(
            new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.Off },
            new VmEntity { Id = VmB, Name = "two", PowerState = PowerState.On });

        await poller.Run();

        Assert.Single(poller.AskedOf(first));
        Assert.Single(poller.AskedOf(second));
        Assert.Equal(PowerState.On, await StateOf(VmA));
        Assert.Equal(PowerState.Off, await StateOf(VmB));
    }

    /// <summary>
    /// One moref reported by two vCenters is two different machines, and each one's own Vm is updated.
    /// A moref is unique only within the vCenter that issued it, so resolving it without the host would
    /// silently point both events at whichever machine happened to be cached first.
    /// </summary>
    [Fact]
    public async Task TheSameMorefFromTwoVcenters_ResolvesPerConnectionAndUpdatesTwoVms()
    {
        var poller = NewPoller();
        var first = poller.Vcenter(VcenterA);
        var second = poller.Vcenter(VcenterB);
        poller.Reports(first, PoweredOn(MorefOne, Earlier));
        poller.Reports(second, PoweredOff(MorefOne, Earlier));
        poller.Resolves(MorefOne, first, VmA);
        poller.Resolves(MorefOne, second, VmB);
        await Seed(
            new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.Off },
            new VmEntity { Id = VmB, Name = "two", PowerState = PowerState.On });

        await poller.Run();

        Assert.Equal(PowerState.On, await StateOf(VmA));
        Assert.Equal(PowerState.Off, await StateOf(VmB));
    }

    #endregion

    #region Events the poller cannot use

    // A moref no connection cache can place is dropped, and the rest of the same batch still lands. The
    // caches are loaded on their own schedule, so a machine created in vCenter since the last cache load
    // raises events this poller cannot yet attribute - and one of those must not cost the whole pass.
    [Fact]
    public async Task AMorefThatCannotBeResolved_IsIgnoredAndTheRestOfTheBatchStillApplies()
    {
        var poller = NewPoller();
        var vcenter = poller.Vcenter(VcenterA);
        poller.Reports(
            vcenter, PoweredOn(MorefTwo, Earlier), PoweredOn(MorefOne, Earlier));
        poller.Resolves(MorefOne, vcenter, VmA);
        await Seed(new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.Off });

        await poller.Run();

        Assert.Equal(PowerState.On, await StateOf(VmA));
        Assert.Empty(poller.Log.At(LogLevel.Error));
    }

    // A moref that resolves to an id with no Vm row behind it is dropped by the query rather than by a
    // check, and the machines in the same batch that do exist are still written. What produces it is a Vm
    // deleted in Player while its vCenter machine is still there and still raising events.
    [Fact]
    public async Task AnEventForAVmThatIsNotInTheDatabase_IsIgnored()
    {
        var poller = NewPoller();
        var vcenter = poller.Vcenter(VcenterA);
        var gone = Guid.NewGuid();
        poller.Reports(
            vcenter, PoweredOn(MorefTwo, Earlier), PoweredOn(MorefOne, Earlier));
        poller.Resolves(MorefTwo, vcenter, gone);
        poller.Resolves(MorefOne, vcenter, VmA);
        await Seed(new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.Off });

        await poller.Run();

        Assert.Equal(PowerState.On, await StateOf(VmA));
        Assert.Empty(poller.Log.At(LogLevel.Error));

        await using var context = NewContext();
        Assert.False(await context.Vms.AnyAsync(x => x.Id == gone, Ct));
    }

    /// <summary>
    /// A machine no event names keeps whatever state it had. The poller writes what vCenter reported and
    /// nothing else, so a pass is never a reason for a machine's indicator to change.
    /// </summary>
    /// <remarks>
    /// Seeded as <c>Suspended</c> rather than <c>On</c> or <c>Off</c> because those are the only two
    /// states this poller can write: a bystander left at a third value is proof that nothing overwrote it
    /// with a default, which a bystander left at <c>Off</c> would not be.
    /// </remarks>
    [Fact]
    public async Task AVmNoEventNames_KeepsTheStateItAlreadyHad()
    {
        var poller = NewPoller();
        var vcenter = poller.Vcenter(VcenterA);
        poller.Reports(vcenter, PoweredOff(MorefOne, Earlier));
        poller.Resolves(MorefOne, vcenter, VmA);
        await Seed(
            new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.On },
            new VmEntity { Id = VmB, Name = "two", PowerState = PowerState.Suspended });

        await poller.Run();

        Assert.Equal(PowerState.Off, await StateOf(VmA));
        Assert.Equal(PowerState.Suspended, await StateOf(VmB));
    }

    /// <summary>
    /// A pass in which no vCenter reported anything - which is nearly every pass in a quiet
    /// installation - leaves every machine as it was.
    /// </summary>
    /// <remarks>
    /// What this does and does not prove is worth being exact about. <c>ProcessEvents</c> returns at
    /// <c>MachineStateService.cs:160</c> before it touches the database at all, and that early return is
    /// **not** what is asserted here: the context comes from the harness's scope and there is no seam on
    /// it, so a version of the method that ran its query and its <c>SaveChangesAsync</c> over an empty
    /// event set would pass this test unchanged. All that is pinned is the outcome - nothing was written -
    /// which is still the thing a user would notice. The cost of the early return not being there is one
    /// query and one no-op save per poll interval, per host.
    /// </remarks>
    [Fact]
    public async Task WhenNoVcenterReportsAnything_NothingIsWritten()
    {
        var poller = NewPoller();
        poller.Vcenter(VcenterA);
        poller.Vcenter(VcenterB);
        await Seed(
            new VmEntity { Id = VmA, Name = "one", PowerState = PowerState.On },
            new VmEntity { Id = VmB, Name = "two", PowerState = PowerState.Suspended });

        await poller.Run();

        Assert.Equal(PowerState.On, await StateOf(VmA));
        Assert.Equal(PowerState.Suspended, await StateOf(VmB));
        Assert.Empty(poller.Log.At(LogLevel.Error));
    }

    #endregion

    #region The window each pass asks for

    /// <summary>
    /// The three event type ids the filter asks vCenter for, and that the window has a begin and no end.
    /// That list is the whole of what this poller will ever see, so it is what a fourth power event type
    /// silently not arriving would show up against.
    /// </summary>
    /// <remarks>
    /// <c>endTimeSpecified</c> is left false, so consecutive windows overlap rather than abut - the same
    /// event can be delivered on two passes. Harmless, because writing a state twice is the same as
    /// writing it once.
    /// </remarks>
    [Fact]
    public async Task TheFilter_AsksForExactlyTheThreePowerEventTypesSinceAMomentWithNoEnd()
    {
        var poller = NewPoller();
        var vcenter = poller.Vcenter(VcenterA);

        await poller.Run();

        var spec = Assert.Single(poller.AskedOf(vcenter));
        Assert.Equal(
            [nameof(VmPoweredOnEvent), nameof(DrsVmPoweredOnEvent), nameof(VmPoweredOffEvent)],
            spec.eventTypeId);
        Assert.True(spec.time.beginTimeSpecified);
        Assert.False(spec.time.endTimeSpecified);
    }

    /// <summary>
    /// The blind spot at startup. The first window a vCenter is given begins at <c>DateTime.UtcNow</c> -
    /// the moment the poller first looked, not the moment the state in the database was last known good -
    /// so a machine powered on or off while the API was down or restarting is never reported and its
    /// indicator stays wrong until the next time that machine changes state.
    /// </summary>
    /// <remarks>
    /// <c>_lastCheckedTimes.GetOrAdd(connection.Address, DateTime.UtcNow)</c> at
    /// <c>MachineStateService.cs:118</c>, and the dictionary is a field of a service the host creates
    /// once, so "first pass" means once per process rather than once per vCenter reconnect. The narrow
    /// fix is to seed the window from something durable rather than from the clock; the honest reading of
    /// the current behaviour is that <c>Vm.PowerState</c> is only claimed to be correct from startup
    /// onward.
    /// </remarks>
    [Fact]
    public async Task TheFirstWindowBeginsAtStartup_SoAPowerEventFromBeforeTheApiStartedIsNeverSeen()
    {
        var poller = NewPoller();
        var vcenter = poller.Vcenter(VcenterA);

        var before = DateTime.UtcNow;
        await poller.Run();
        var after = DateTime.UtcNow;

        var spec = Assert.Single(poller.AskedOf(vcenter));
        Assert.InRange(spec.time.beginTime, before, after);
    }

    /// <summary>
    /// Once a vCenter has answered, the next window begins when the pass that asked it <em>started</em>
    /// rather than when it finished - so events vCenter raised while the pass was in flight are still
    /// inside the following window instead of falling into the gap between them.
    /// </summary>
    /// <remarks>
    /// Three passes rather than two, and that is a finding rather than caution. <c>GetEvents</c> reads
    /// the stored time at <c>MachineStateService.cs:118</c> and captures the replacement at <c>:120</c>,
    /// one statement apart, so on the very first pass the value asked for and the value stored are the
    /// same instant: passes one and two ask for the same window, and the first window that has actually
    /// moved is the third's. Comparing only the first two proves nothing.
    /// </remarks>
    [Fact]
    public async Task ASubsequentWindow_BeginsWhenThePreviousPassStartedNotWhenItFinished()
    {
        var ct = Ct;
        var poller = NewPoller();
        var vcenter = poller.Vcenter(VcenterA);
        var askedAt = new List<DateTime>();

        // A pass whose vCenter call takes long enough to tell its start from its finish. Without it the
        // two candidate begin times are microseconds apart and no assertion can separate them.
        poller.Vsphere.GetEvents(Arg.Any<EventFilterSpec>(), vcenter).Returns(async _ =>
        {
            askedAt.Add(DateTime.UtcNow);
            await Task.Delay(SlowPass, ct);
            return Enumerable.Empty<Event>();
        });

        await poller.Run(passes: 3);

        var specs = poller.AskedOf(vcenter);
        Assert.Equal(3, specs.Count);

        // The third window moved on from the second, and it begins no later than the moment the second
        // pass asked - which is what makes it the second pass's start and not its finish, a whole
        // SlowPass later.
        Assert.True(
            specs[2].time.beginTime > specs[1].time.beginTime,
            "the window has to move once a vCenter has answered");
        Assert.True(
            specs[2].time.beginTime <= askedAt[1],
            $"window began {specs[2].time.beginTime:O}, pass 2 asked at {askedAt[1]:O}");
    }

    #endregion

    #region When a vCenter cannot be reached

    /// <summary>
    /// A vCenter whose event query fails is logged and contributes nothing - and its window is not
    /// advanced, so the events it did not manage to answer with are still inside the window the next pass
    /// asks for. This is the failure mode worth the most in this file: an advanced window would drop
    /// every power event raised during an outage, permanently, and leave the indicator wrong with nothing
    /// in the log to connect the two.
    /// </summary>
    /// <remarks>
    /// The window is what the three passes are for. All three ask for the identical begin time, which is
    /// only true because <c>MachineStateService.cs:132</c> is after the <c>catch</c> that returns at
    /// <c>:129</c>; nothing else in the class says so.
    /// </remarks>
    [Fact]
    public async Task WhenAVcenterCannotBeReached_ItIsLoggedAndItsWindowIsNotAdvanced()
    {
        var poller = NewPoller();
        var vcenter = poller.Vcenter(VcenterA);
        var unreachable = new TimeoutException("the vCenter did not answer");
        poller.Fails(vcenter, unreachable);

        await poller.Run(passes: 3);

        var specs = poller.AskedOf(vcenter);
        Assert.Equal(3, specs.Count);
        Assert.Single(specs.Select(x => x.time.beginTime).Distinct());

        // Loudly, and naming the vCenter - unlike the loop-level catch below.
        Assert.Equal(3, poller.Log.At(LogLevel.Error).Count());
        Assert.All(
            poller.Log.At(LogLevel.Error),
            x =>
            {
                Assert.Same(unreachable, x.Exception);
                Assert.Contains(VcenterA, x.Message);
            });
    }

    // One vCenter being unreachable does not cost the pass: the other's events are still applied, in the
    // same pass. An installation with two vCenters keeps a correct indicator for the machines on the one
    // that is up, which is the reason each connection is queried in its own try rather than the pass in one.
    [Fact]
    public async Task WhenOneVcenterCannotBeReached_TheOthersEventsInTheSamePassStillApply()
    {
        var poller = NewPoller();
        var down = poller.Vcenter(VcenterA);
        var up = poller.Vcenter(VcenterB);
        poller.Fails(down, new TimeoutException("the vCenter did not answer"));
        poller.Reports(up, PoweredOn(MorefOne, Earlier));
        poller.Resolves(MorefOne, up, VmB);
        await Seed(new VmEntity { Id = VmB, Name = "two", PowerState = PowerState.Off });

        await poller.Run();

        Assert.Equal(PowerState.On, await StateOf(VmB));
        Assert.Single(poller.Log.At(LogLevel.Error));
    }

    /// <summary>
    /// What a failure of the pass as a whole - rather than of one vCenter - is logged at:
    /// <c>LogDebug</c>. So a <c>MachineStateService</c> that is failing every pass in production is
    /// invisible, because no deployment runs at Debug: the power indicator quietly stops following
    /// anything and the log says nothing at any level an operator looks at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a diagnosability defect and not a matter of preference. The catch at
    /// <c>MachineStateService.cs:77</c> is the only one of the four pollers' loop-level catches that is
    /// not <c>LogError</c>, and the per-connection catch nine lines below it, at <c>:128</c>, is
    /// <c>LogError</c> - so the same class already treats the smaller failure as the louder one.
    /// Changing <c>:77</c> to <c>LogError</c> is the fix, and this test is what will turn red when
    /// somebody makes it.
    /// </para>
    /// <para>
    /// Reached here by failing <c>IConnectionService.GetAllConnections</c>, which is the pass's first
    /// call and outside every inner try. In production the same catch takes a failed scope resolution, a
    /// moref lookup that threw, and any failure of the query or the save.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhenAWholePassFails_ItIsSwallowedAndLoggedAtDebugWhereNothingWillSeeIt()
    {
        var poller = NewPoller();
        var broken = new InvalidOperationException("the connection list is not available");
        poller.Connections.GetAllConnections().Throws(broken);

        await poller.Run();

        // Matched by reference: PollLoop refuses the passes after the one it allowed, and the service
        // logs those refusals through this same catch and at this same level.
        Assert.Contains(broken, poller.Log.At(LogLevel.Debug).Select(x => x.Exception));
        Assert.Empty(poller.Log.At(LogLevel.Error));
        Assert.Empty(poller.Log.At(LogLevel.Critical));
        Assert.Empty(poller.Log.At(LogLevel.Warning));
        Assert.Empty(poller.Log.At(LogLevel.Information));
    }

    #endregion

    #region The loop itself

    /// <summary>
    /// <c>CheckState</c> brings the next poll forward instead of leaving the caller to wait out the
    /// interval. That is how a power command submitted through the API shows up on the indicator
    /// promptly - the <c>CheckTasks</c> pipeline behavior calls it after the command is sent - and with a
    /// shipped interval of five seconds it is the difference between an indicator that follows a click
    /// and one that lags it.
    /// </summary>
    /// <remarks>
    /// Every other test in this class depends on this through <see cref="PollLoop.Run"/>, which nudges to
    /// get its second and later passes. This one says it on its own: the loop is left sitting on a
    /// minute-long wait, shown to stay there, and then nudged.
    /// </remarks>
    [Fact]
    public async Task CheckState_BringsTheNextPollForwardRatherThanWaitingOutTheInterval()
    {
        var poller = NewPoller();
        poller.Vcenter(VcenterA);
        poller.Loop.AllowedPasses = 2;

        await poller.Service.StartAsync(Ct);

        try
        {
            await PollLoop.Until(() => poller.Loop.Passes >= 1, "the pass a start does on its own");

            // The interval is a minute, so nothing but a nudge can produce a second pass.
            await Task.Delay(200, Ct);
            Assert.Equal(1, poller.Loop.Passes);

            poller.Service.CheckState();

            await PollLoop.Until(() => poller.Loop.Passes >= 2, "a second pass after CheckState");
        }
        finally
        {
            poller.Service.CheckState();
            await poller.Service.StopAsync(Ct);
        }
    }

    /// <summary>
    /// The interval between polls is <c>Vsphere:CheckTaskProgressIntervalMilliseconds</c> - the vSphere
    /// <em>task</em> poller's setting, reused. There is no machine-state interval of its own, so an
    /// operator who slows down task progress polling to spare a busy vCenter also slows down how fast the
    /// power indicator notices anything, and cannot separate the two.
    /// </summary>
    /// <remarks>
    /// Driven through <see cref="PollLoop.RunUnprompted"/>, the one entry point that lets the interval
    /// rather than a nudge turn the loop, with the setting under test at a few milliseconds and every
    /// other interval on <c>VsphereOptions</c> at a minute - so the margin between passing and failing is
    /// four orders of magnitude rather than a hair.
    /// </remarks>
    [Fact]
    public async Task TheIntervalItWaitsIsTheTaskPollersOwnSetting_NotOneOfItsOwn()
    {
        var poller = NewPoller(interval: 25);
        poller.Vcenter(VcenterA);

        await poller.Loop.RunUnprompted(poller.Service, poller.Service.CheckState);

        Assert.True(poller.Loop.Passes >= 2);
    }

    #endregion

    #region Arrangement

    private Poller NewPoller(int interval = NeverOnItsOwn) => new(NewContext, interval);

    private async Task<PowerState> StateOf(Guid id)
    {
        await using var context = NewContext();

        return (await context.Vms.SingleAsync(x => x.Id == id, Ct)).PowerState;
    }

    private static ManagedObjectReference Mor(string moref) =>
        new() { type = "VirtualMachine", Value = moref };

    private static Event PoweredOn(string moref, DateTime at) => Evt<VmPoweredOnEvent>(moref, at);

    private static Event PoweredOff(string moref, DateTime at) => Evt<VmPoweredOffEvent>(moref, at);

    private static Event Evt<T>(string moref, DateTime at) where T : Event, new() =>
        Fill(new T(), moref, at);

    private static Event Evt(Type eventType, string moref, DateTime at) =>
        Fill((Event)Activator.CreateInstance(eventType), moref, at);

    // The three fields the poller reads off an event: its runtime type, the moref of the machine it is
    // about, and when vCenter says it happened. Nothing else on Event is looked at.
    private static Event Fill(Event evt, string moref, DateTime at)
    {
        evt.vm = new VmEventArgument { vm = Mor(moref) };
        evt.createdTime = at;

        return evt;
    }

    /// <summary>
    /// One <c>MachineStateService</c> over whatever vCenters a test registers, standing in for what
    /// <c>ConnectionService</c> would have cached from live connections.
    /// </summary>
    private sealed class Poller
    {
        public readonly IVsphereService Vsphere = Substitute.For<IVsphereService>();
        public readonly IConnectionService Connections = Substitute.For<IConnectionService>();
        public readonly RecordingLogger<MachineStateService> Log = new();
        public readonly PollLoop Loop;
        public readonly MachineStateService Service;

        private readonly List<VsphereConnection> _connections = [];

        public Poller(Func<VmContext> newContext, int interval)
        {
            Loop = new PollLoop(newContext, Vsphere);

            var options = Substitute.For<IOptionsMonitor<VsphereOptions>>();
            options.CurrentValue.Returns(new VsphereOptions
            {
                CheckTaskProgressIntervalMilliseconds = interval,

                // Every other interval on the options object, so that a poller reading the wrong one
                // fails by never turning rather than by turning at the right rate for the wrong reason.
                ReCheckTaskProgressIntervalMilliseconds = NeverOnItsOwn,
                ConnectionRetryIntervalSeconds = NeverOnItsOwn,
                ConnectionRefreshIntervalMinutes = NeverOnItsOwn,
                TaskPollIntervalMilliseconds = NeverOnItsOwn,
            });

            // Read afresh each pass, so a test can register a vCenter before it starts the loop.
            Connections.GetAllConnections().Returns(_ => _connections.ToArray());

            Service = new MachineStateService(options, Log, Connections, Loop);
        }

        /// <summary>A vCenter the poller will find in the connection list, quiet until told otherwise.</summary>
        public VsphereConnection Vcenter(string address, bool enabled = true)
        {
            var connection = new VsphereConnection(
                new VsphereHost { Enabled = enabled, Address = address },
                new VsphereOptions(),
                NullLogger.Instance)
            {
                // Nothing dials it. The poller reads only Enabled and Address off a connection and hands
                // the whole object to IVsphereService, which is substituted; the client is set because a
                // real connection always has one, not because anything here calls it.
                Client = Substitute.For<IVimClient>(),
            };

            _connections.Add(connection);

            // Answering nothing by default rather than leaving the call unstubbed: NSubstitute would
            // hand back a completed task holding a null enumerable, which is not something a vCenter can
            // do and which the pass would then fail on for a reason no test meant.
            Reports(connection);

            return connection;
        }

        /// <summary>What this vCenter answers with when it is asked for events.</summary>
        public void Reports(VsphereConnection connection, params Event[] events) =>
            Vsphere.GetEvents(Arg.Any<EventFilterSpec>(), connection)
                .Returns(Task.FromResult<IEnumerable<Event>>(events));

        /// <summary>A vCenter that cannot be reached at all.</summary>
        public void Fails(VsphereConnection connection, Exception ex) =>
            Vsphere.GetEvents(Arg.Any<EventFilterSpec>(), connection).Throws(ex);

        /// <summary>
        /// A moref this vCenter's connection cache can place, which is the only thing that turns a
        /// vCenter event into a Player Vm.
        /// </summary>
        public void Resolves(string moref, VsphereConnection connection, Guid id) =>
            Connections.GetVmIdByRef(moref, connection.Address).Returns(id);

        /// <summary>
        /// The filter specs this vCenter has been handed, in the order the passes handed them over, which
        /// is how the window a pass asked for is asserted.
        /// </summary>
        public IReadOnlyList<EventFilterSpec> AskedOf(VsphereConnection connection) =>
            Vsphere.ReceivedCalls()
                .Where(x => x.GetMethodInfo().Name == nameof(IVsphereService.GetEvents))
                .Select(x => x.GetArguments())
                .Where(x => ReferenceEquals(x[1], connection))
                .Select(x => (EventFilterSpec)x[0])
                .ToList();

        public Task Run(int passes = 1) => Loop.Run(Service, Service.CheckState, passes);
    }

    #endregion
}
