// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Crucible.Common.EntityEvents.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// <c>ProxmoxStateService</c>, the poller behind the power indicator on every Proxmox machine. Each pass
/// reads the whole cluster's resource list and reconciles three columns from it - <c>PowerState</c>,
/// <c>Type</c> and the stored <c>ProxmoxVmInfo.Node</c> - so a machine started, stopped or migrated
/// outside Player shows correctly in Player, and so does one this API powered on itself: nothing on the
/// command path writes <c>PowerState</c>, which makes this service the only thing that ever does.
/// </summary>
/// <remarks>
/// <para>
/// Driven over <see cref="PollLoop"/>, which is what makes a loop with no return value assertable: the
/// interval is configured as a minute so nothing but the harness's own nudge advances it, and a pass is
/// counted by the scope it creates. The three interval tests are the exception, and are the only ones here
/// that depend on wall clock.
/// </para>
/// <para>
/// What the cluster answers is not hand-built. <c>ClusterResource.IsRunning</c>, <c>IsStopped</c> and
/// <c>IsPaused</c> - the three flags <c>GetPowerState</c> reads - are plain settable booleans that the
/// Proxmox client's deserializer fills in from PVE's <c>status</c> string; they are <em>not</em> computed
/// from the <c>Status</c> property, so an object built in a test with <c>Status = "stopped"</c> reports
/// <c>PowerState.Unknown</c>. Setting the flags directly instead would let this file decide the answer to
/// the question it is asking. So every resource here comes out of <see cref="FakeProxmoxCluster"/> through
/// the real client - see <see cref="Reporting"/> - and the status words below are Proxmox's own.
/// </para>
/// <para>
/// <c>IProxmoxService</c> itself is substituted, because the failures worth covering are the ones a
/// cluster produces and a fake HTTP handler cannot: <c>GetVms</c> throwing, and a vmid listed twice.
/// </para>
/// <para>
/// Read against <c>ProxmoxTaskServiceTests</c>. The two Proxmox pollers run side by side over the same
/// table and each owns a different part of it, which is why several assertions here are about what a pass
/// leaves alone: this one never touches <c>HasPendingTasks</c>, and the task poller never touches
/// <c>PowerState</c>. They also disagree about which rows are theirs - this one sweeps
/// <c>ProxmoxVmInfo != null</c>, the task poller sweeps <c>Type == VmType.Proxmox</c> - and the
/// consequence of the difference is pinned by
/// <see cref="AVmWithProxmoxInfoButAnotherType_IsRetypedAsProxmox"/>.
/// </para>
/// <para>
/// <c>UpdateVm</c> is a second way in, off the poll loop: <c>ProxmoxService.ResolveNode</c> calls it when
/// a command discovers that a machine has migrated, so one machine's state is written without waiting for
/// the next pass. It runs on an unbounded <c>ActionBlock</c> and creates a scope per item, which means
/// those tests are counted by the same <see cref="PollLoop.AllowedPasses"/> barrier - and that a refused
/// pass is how a queued item is made to fail.
/// </para>
/// </remarks>
public class ProxmoxStateServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>
    /// A minute, so that only the harness's nudge can turn the loop. See <see cref="PollLoop"/>: if the
    /// interval were what advanced it, every test here would be a race rather than an assertion.
    /// </summary>
    private const int NeverOnItsOwn = 60;

    /// <summary>
    /// One second - the shortest interval this service will accept, and the floor it clamps anything
    /// smaller to. Unlike the two millisecond-configured pollers, "soon" here cannot be 25ms.
    /// </summary>
    private const int AtOnce = 1;

    private const int Vmid = 100;
    private const int OtherVmid = 101;

    /// <summary>A vmid on the cluster that Player has no row for, which most of a real cluster is.</summary>
    private const int UntrackedVmid = 999;

    private const string Node = FakeProxmoxCluster.DefaultNode;
    private const string OtherNode = "pve2";

    private static readonly Guid VmA = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid VmB = new("bbbbbbbb-0000-4000-8000-000000000002");

    #region The enabled switch

    /// <summary>
    /// With <c>Proxmox:Enabled</c> false the pass does nothing at all - it does not even create a scope,
    /// so no database is read and no cluster is dialed. That is what lets a deployment with no Proxmox
    /// cluster register this service without it failing on every turn.
    /// </summary>
    /// <remarks>
    /// The gate is inside the loop and ahead of <c>CreateScope</c>, so <see cref="PollLoop"/>'s refusal
    /// barrier never trips and there is no effect to wait <em>for</em> - only one to wait out. The debug
    /// entry is what says a turn happened at all, which is what makes zero passes evidence about the gate
    /// rather than about the loop.
    /// </remarks>
    [Fact]
    public async Task WhenProxmoxIsDisabled_APassCreatesNoScopeAndAsksTheClusterNothing()
    {
        var h = Build(enabled: false, interval: AtOnce);
        await SeedVm(VmA, Vmid, PowerState.On);
        h.Returns(await Reporting(Vmid, "stopped"));

        await h.Service.StartAsync(Ct);
        h.Service.CheckState();
        await Task.Delay(250, Ct);
        await h.Service.StopAsync(Ct);

        Assert.Equal(0, h.Loop.Passes);
        await h.Proxmox.DidNotReceive().GetVms();
        Assert.Contains(h.Log.At(LogLevel.Debug), x => x.Message == "Proxmox disabled, skipping");

        // And the state it would have corrected stands: whatever the last enabled pass decided is what
        // the UI keeps showing.
        Assert.Equal(PowerState.On, (await Reread(VmA)).PowerState);
    }

    /// <summary>
    /// The switch is read on every pass rather than once at construction, so turning Proxmox on takes
    /// effect without restarting the API - which is the whole reason this service holds an
    /// <c>IOptionsMonitor</c> rather than the options object.
    /// </summary>
    [Fact]
    public async Task TheEnabledSwitchIsReadEveryPass_SoTurningProxmoxOnNeedsNoRestart()
    {
        var h = Build(enabled: false);
        await SeedVm(VmA, Vmid);
        h.Returns(await Reporting(Vmid));

        await h.Service.StartAsync(Ct);

        try
        {
            // Long enough for the turn that start begins - which does no work at all when disabled - to
            // be over before the switch moves, so the debug entry asserted below is that turn's.
            await Task.Delay(100, Ct);
            h.Options.Enabled = true;

            // Two, not one: Passes counts a pass that has *started*, so waiting for the second to be
            // asked for is what proves the first one finished its save. AllowedPasses is 1, so that
            // second scope is refused and cannot write anything of its own.
            await PollLoop.Until(
                () =>
                {
                    h.Service.CheckState();

                    return h.Loop.Passes >= 2;
                },
                "a pass after the switch was turned on");
        }
        finally
        {
            await h.Service.StopAsync(Ct);
        }

        Assert.Contains(h.Log.At(LogLevel.Debug), x => x.Message == "Proxmox disabled, skipping");
        Assert.Equal(PowerState.On, (await Reread(VmA)).PowerState);
    }

    #endregion

    #region Reconciling power state

    /// <summary>
    /// PVE's status vocabulary, mapped onto the four power states Player has. This is the service's
    /// reason to exist, and the whole chain is real: the words below are what <c>/cluster/resources</c>
    /// sends, the Proxmox client turns them into its power flags, and <c>GetPowerState</c> reads those.
    /// </summary>
    /// <remarks>
    /// <c>unknown</c> is the one that is not a state of the machine: PVE reports it for a guest whose node
    /// the cluster cannot currently talk to. It is covered again by
    /// <see cref="AMachineTheClusterCallsUnknown_LosesTheStateItHad"/>, because arriving at
    /// <c>Unknown</c> from <c>Unknown</c> proves less than being taken back to it.
    /// </remarks>
    [Theory]
    [InlineData("running", PowerState.On)]
    [InlineData("stopped", PowerState.Off)]
    [InlineData("paused", PowerState.Suspended)]
    [InlineData("unknown", PowerState.Unknown)]
    public async Task TheStatusTheClusterReports_BecomesThePowerStatePlayerShows(
        string status, PowerState expected)
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(await Reporting(Vmid, status));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        Assert.Equal(expected, (await Reread(VmA)).PowerState);
    }

    /// <summary>
    /// A machine that has moved node has its stored node rewritten, which is the other half of this
    /// service's job and the less obvious one. <c>ProxmoxVmInfo.Node</c> is part of the address of every
    /// per-machine call the API makes - <c>Nodes[Node].Qemu[Id]</c> - so a stale node means every power
    /// operation, console request and ISO mount for that machine is sent to a node it has left.
    /// </summary>
    /// <remarks>
    /// The comment saying this poller is the only thing that refreshes it is on
    /// <c>ProxmoxService.ResolveNode</c>, which is the workaround a command uses when it hits the stale
    /// value first; <see cref="FakeProxmoxCluster.Migrates"/> describes the same state from the other side.
    /// </remarks>
    [Fact]
    public async Task AMachineThatHasMovedNode_HasItsStoredNodeRewritten()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, node: Node);
        h.Returns(await Reporting(Vmid, node: OtherNode));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        Assert.Equal(OtherNode, (await Reread(VmA)).ProxmoxVmInfo.Node);
    }

    /// <summary>
    /// A machine the cluster no longer lists keeps the state it last had, forever. Characterized rather
    /// than endorsed: a Vm deleted in PVE but still in Player's database goes on showing as powered on,
    /// and nothing else will ever move it, because the poller only writes a row it found a resource for.
    /// </summary>
    /// <remarks>
    /// Read against <see cref="AMachineTheClusterCallsUnknown_LosesTheStateItHad"/>: a machine the cluster
    /// says it cannot see loses its state, and a machine the cluster does not mention keeps it. The debug
    /// entry is the pass saying it noticed the mismatch and did nothing about it, and the absence of a
    /// failure is what tells "left alone" from "threw on the way past" - which look the same from the row.
    /// </remarks>
    [Fact]
    public async Task AMachineTheClusterNoLongerReports_KeepsTheStateItLastHad()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, PowerState.On);

        await h.Loop.Run(h.Service, h.Service.CheckState);

        Assert.Equal(PowerState.On, (await Reread(VmA)).PowerState);
        Assert.Empty(Failures(h));
        Assert.Contains(
            h.Log.At(LogLevel.Debug),
            x => x.Message == "Found 0 machines in PVE and 1 machine in database.");
    }

    /// <summary>
    /// A machine PVE reports as <c>unknown</c> loses the state it had. That is the right answer and worth
    /// pinning: <c>unknown</c> is what a guest on an unreachable node reports, so a node dropping out of
    /// the cluster greys the power indicator out for its machines rather than leaving them looking on.
    /// </summary>
    [Fact]
    public async Task AMachineTheClusterCallsUnknown_LosesTheStateItHad()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, PowerState.On);
        h.Returns(await Reporting(Vmid, "unknown"));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        Assert.Equal(PowerState.Unknown, (await Reread(VmA)).PowerState);
    }

    /// <summary>
    /// A Vm with no <c>ProxmoxVmInfo</c> is not considered at all, which is what keeps this poller off
    /// vSphere's machines. The filter is load-bearing rather than an optimization: the loop reads
    /// <c>dbVm.ProxmoxVmInfo.Id</c> unconditionally, so a vSphere Vm reaching it would throw and cost
    /// every Proxmox machine in the same pass its update.
    /// </summary>
    [Fact]
    public async Task AVmWithNoProxmoxInfo_IsNotConsidered()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        await SeedVsphereVm(VmB, PowerState.On);
        h.Returns(await Reporting(Vmid));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        var vsphere = await Reread(VmB);
        Assert.Equal(VmType.Vsphere, vsphere.Type);
        Assert.Equal(PowerState.On, vsphere.PowerState);

        // The Proxmox machine beside it was still updated, and nothing was logged as a failure - both of
        // which a dropped filter would have taken away.
        Assert.Equal(PowerState.On, (await Reread(VmA)).PowerState);
        Assert.Empty(Failures(h));
    }

    /// <summary>
    /// A Vm carrying Proxmox info under some other <c>Type</c> is retyped as Proxmox, and this is the
    /// only thing in the application that does it. The write is unconditional, so it also repairs a row
    /// created wrong.
    /// </summary>
    /// <remarks>
    /// Which makes this the way out of the trap door
    /// <c>ProxmoxTaskServiceTests.AVmWithProxmoxInfoButAnotherType_IsFlaggedAndThenNeverCleared</c>
    /// describes: the task poller flags such a Vm through <c>ProxmoxService</c> but its own sweep filters
    /// on <c>Type == VmType.Proxmox</c>, so it can never clear what it set. Pinning both sides says that
    /// the state poller has to have run at least once for a Proxmox Vm's power buttons to work at all.
    /// </remarks>
    [Fact]
    public async Task AVmWithProxmoxInfoButAnotherType_IsRetypedAsProxmox()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, type: VmType.Unknown);
        h.Returns(await Reporting(Vmid));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        var vm = await Reread(VmA);
        Assert.Equal(VmType.Proxmox, vm.Type);
        Assert.Equal(PowerState.On, vm.PowerState);
    }

    /// <summary>
    /// A vmid the cluster lists twice costs nothing. <c>/cluster/resources</c> can report the same guest
    /// on two nodes while a migration is in flight, and the pass indexes the list by vmid - so without
    /// the <c>DistinctBy</c> ahead of the <c>ToDictionary</c> one duplicated guest would throw and cost
    /// every machine in the cluster its update, on every pass, until the migration finished.
    /// </summary>
    /// <remarks>
    /// Which of the two wins is <c>DistinctBy</c>'s answer - the first - and not something the service
    /// chooses; asserted only so that a change in that order is noticed here rather than in a UI showing
    /// a machine's state flickering.
    /// </remarks>
    [Fact]
    public async Task AVmidTheClusterListsTwice_IsTakenOnceRatherThanThrowing()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(await Reporting((Vmid, "running", Node), (Vmid, "stopped", OtherNode)));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        var vm = await Reread(VmA);
        Assert.Equal(PowerState.On, vm.PowerState);
        Assert.Equal(Node, vm.ProxmoxVmInfo.Node);
        Assert.Empty(Failures(h));
    }

    /// <summary>
    /// A vmid Player does not track is dropped, and no row is invented for it. The resource list is
    /// cluster-wide, so on a shared cluster most of what a pass reads is machines this API knows nothing
    /// about - the poller reconciles from Player's rows outwards, never from PVE's list inwards.
    /// </summary>
    [Fact]
    public async Task AVmidPlayerDoesNotTrack_IsIgnoredAndNoRowIsCreatedForIt()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(await Reporting((Vmid, "running", Node), (UntrackedVmid, "running", Node)));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        Assert.Equal(PowerState.On, (await Reread(VmA)).PowerState);

        await using var context = NewContext();
        Assert.Equal(1, await context.Vms.CountAsync(Ct));
    }

    /// <summary>
    /// A pass leaves <c>HasPendingTasks</c> alone. The two Proxmox pollers write the same table and each
    /// owns its own column: this one would otherwise clear the flag that greys the power buttons out
    /// while an operation is still running, on the pass after the one that set it.
    /// </summary>
    [Fact]
    public async Task APass_LeavesHasPendingTasksToTheTaskPoller()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, PowerState.Off, pending: true);
        h.Returns(await Reporting(Vmid));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        var vm = await Reread(VmA);
        Assert.Equal(PowerState.On, vm.PowerState);
        Assert.True(vm.HasPendingTasks);
    }

    /// <summary>
    /// A state change is published as an entity update, which is how a machine powered on from the PVE
    /// web UI reaches a browser: the poller saves, the interceptor publishes, and
    /// <c>VmUpdatedSignalRHandler</c> sends the changed property names to every group watching that Vm.
    /// Without this the indicator would only correct itself on a page reload.
    /// </summary>
    [Fact]
    public async Task APowerStateChange_IsPublishedSoTheBrowserHearsAboutIt()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, PowerState.Off);
        h.Returns(await Reporting(Vmid));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        var update = TheUpdate();
        Assert.Equal(VmA, update.Entity.Id);
        Assert.Equal([nameof(VmEntity.PowerState)], update.ModifiedProperties);
    }

    /// <summary>
    /// A pass that changed something says so at Information, counting the rows it wrote.
    /// </summary>
    [Fact]
    public async Task APassThatChangedSomething_SaysSoAtInformation()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        await SeedVm(VmB, OtherVmid);
        h.Returns(await Reporting((Vmid, "running", Node), (OtherVmid, "stopped", Node)));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        Assert.Contains(h.Log.At(LogLevel.Information), x => x.Message == "Updated 2 machines");
    }

    /// <summary>
    /// A pass that changed nothing stays at Debug. This is what keeps an idle cluster out of the log: the
    /// poller runs on a handful of seconds forever, so an Information entry per pass would be the loudest
    /// thing a deployment produces. EF is what makes it possible - a row assigned the values it already
    /// has is not Modified, so the count is zero without the poller comparing anything itself.
    /// </summary>
    [Fact]
    public async Task APassThatChangedNothing_StaysAtDebug()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, PowerState.On);
        h.Returns(await Reporting(Vmid));

        await h.Loop.Run(h.Service, h.Service.CheckState);

        Assert.Empty(h.Log.At(LogLevel.Information));
        Assert.Contains(h.Log.At(LogLevel.Debug), x => x.Message == "Updated 0 machines");
    }

    #endregion

    #region When the next pass comes

    /// <summary>
    /// With an interval configured the next pass arrives on it, unasked. Nothing nudges the loop here -
    /// see <see cref="PollLoop.RunUnprompted"/> - so a second pass happening at all can only be the
    /// configured second being used rather than the minute the other tests rely on.
    /// </summary>
    [Fact]
    public async Task WithAnIntervalConfigured_TheNextPassArrivesOnItUnasked()
    {
        var h = Build(interval: AtOnce);
        await SeedVm(VmA, Vmid);
        h.Returns(await Reporting(Vmid));

        await h.Loop.RunUnprompted(h.Service, h.Service.CheckState);

        Assert.True(h.Loop.Passes >= 2);

        // One second is a legal interval, so nothing was clamped and nothing was complained about.
        Assert.Empty(Warnings(h));
    }

    /// <summary>
    /// An unset <c>StateRefreshIntervalSeconds</c> binds to zero, which is floored to one second and
    /// warned about once. Without the floor the wait would be built from <c>TimeSpan.Zero</c> and cancel
    /// immediately, turning the poller into a tight loop hammering the PVE API and this database as fast
    /// as it can - and the option has no default, so that is what a deployment that never set it gets.
    /// </summary>
    /// <remarks>
    /// The elapsed-time assertion is the only one that can tell the floor from the busy loop, and it is a
    /// lower bound on purpose: a machine slow enough to make it flaky would have to take longer than a
    /// second to schedule two passes, where the failure it guards against is two passes in microseconds.
    /// The single warning across two passes is the other half - a warning per pass would be the same
    /// flood the misconfiguration itself causes.
    /// </remarks>
    [Fact]
    public async Task WithNoIntervalConfigured_ItFloorsAtOneSecondAndWarnsOnce()
    {
        var h = Build(interval: 0);
        h.Loop.AllowedPasses = 2;

        var clock = Stopwatch.StartNew();
        await h.Service.StartAsync(Ct);

        try
        {
            await PollLoop.Until(() => h.Loop.Passes >= 2, "a second pass on the floored interval");
        }
        finally
        {
            await h.Service.StopAsync(Ct);
        }

        Assert.True(
            clock.Elapsed.TotalMilliseconds > 500,
            $"The second pass came after {clock.Elapsed.TotalMilliseconds:N0}ms, so nothing waited.");

        var warning = Assert.Single(Warnings(h));
        Assert.Contains("StateRefreshIntervalSeconds is 0", warning.Message);
        Assert.Contains("Using 1 second(s) instead", warning.Message);
    }

    /// <summary>
    /// The warning is per bad value rather than once per process: a corrected interval clears the memory
    /// of the bad one, so breaking it again is reported again. Otherwise an operator who fixed the value,
    /// watched the warning stop, and then broke it in a later edit would get no second warning - and this
    /// service reloads its options while running, so that sequence needs no restart to happen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every wait here is counted off the fact that the interval is read at the <em>end</em> of a pass,
    /// which the loop being sequential turns into two exact statements. Waiting for pass N+1 to start
    /// proves read N has happened - which is what makes the first warning the unset value's and not a
    /// race with the edit below it. And read N+2 is a whole interval after an edit whatever read N+1 saw,
    /// so waiting two more passes after an edit proves some read used the new value.
    /// </para>
    /// <para>
    /// One second is the corrected value because it has to be both legal and short: this test pays it
    /// once per pass, and there is no shorter legal interval to pay.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnIntervalCorrectedAndThenBrokenAgain_IsWarnedAboutAgain()
    {
        var h = Build(interval: 0);
        h.Loop.AllowedPasses = 50;

        await h.Service.StartAsync(Ct);

        try
        {
            await PollLoop.Until(() => h.Loop.Passes >= 2, "the first pass to read the unset interval");

            h.Options.StateRefreshIntervalSeconds = AtOnce;
            await PollLoop.Until(() => h.Loop.Passes >= 4, "a pass to read the corrected interval");

            h.Options.StateRefreshIntervalSeconds = 0;
            await PollLoop.Until(() => h.Loop.Passes >= 6, "a pass to read it broken again");
        }
        finally
        {
            await h.Service.StopAsync(Ct);
        }

        Assert.Equal(2, Warnings(h).Count());
    }

    /// <summary>
    /// <c>CheckState</c> brings the next pass forward instead of waiting the interval out. Nothing in the
    /// application calls it today, which is exactly why it is worth a test of its own: every other test
    /// in this class depends on it through <see cref="PollLoop.Run"/>, and a class whose whole harness
    /// stopped working would not report a broken <c>CheckState</c> clearly.
    /// </summary>
    [Fact]
    public async Task CheckState_BringsTheNextPassForwardRatherThanWaitingOutTheInterval()
    {
        var h = Build();
        h.Loop.AllowedPasses = 2;

        await h.Service.StartAsync(Ct);

        try
        {
            await PollLoop.Until(() => h.Loop.Passes >= 1, "the first pass");

            // The interval is a minute, so the loop is now asleep and no second pass can arrive on its
            // own inside the bounded wait below.
            h.Service.CheckState();
            await PollLoop.Until(() => h.Loop.Passes >= 2, "a second pass after CheckState");
        }
        finally
        {
            await h.Service.StopAsync(Ct);
        }

        await h.Proxmox.Received(2).GetVms();
    }

    #endregion

    #region Failures the loop swallows

    /// <summary>
    /// A cluster that cannot be reached is logged at Error with the exception and costs the pass, and the
    /// loop keeps turning. Without the <c>catch</c> the first failure would fault the execute task and no
    /// machine's power state would ever be corrected again until the API was restarted - and the service
    /// would still report itself as healthy, because a faulted <c>BackgroundService</c> does not stop the
    /// host.
    /// </summary>
    [Fact]
    public async Task WhenTheClusterCannotBeReached_TheFailureIsLoggedAndTheLoopKeepsTurning()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, PowerState.On);
        h.Proxmox.GetVms().Returns<Task<IEnumerable<IClusterResourceVm>>>(
            _ => throw new HttpRequestException("no route to host"));

        await h.Loop.Run(h.Service, h.Service.CheckState, passes: 2);

        // Two passes, so the second one is the evidence the first did not stop the loop.
        await h.Proxmox.Received(2).GetVms();

        var errors = h.Log.At(LogLevel.Error).Where(x => x.Exception is HttpRequestException).ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, x => Assert.Contains("Exception in ProxmoxStateService", x.Message));

        // Nothing was reconciled, which is the right answer to "the cluster did not say": a machine reset
        // to Unknown on a failed read would grey out a console the user is working in.
        Assert.Equal(PowerState.On, (await Reread(VmA)).PowerState);
    }

    #endregion

    #region UpdateVm, the way in that does not wait for a pass

    /// <summary>
    /// <c>UpdateVm</c> writes one machine's state without a polling pass. <c>ProxmoxService.ResolveNode</c>
    /// is what calls it: a command that found the stored node stale has just been handed the live
    /// resource, so it hands it back here rather than leaving the row wrong until the next sweep.
    /// </summary>
    /// <remarks>
    /// The service is never started - the queue is built in the constructor, so this path does not need
    /// the loop at all - and <c>StopAsync</c> is what makes the assertion deterministic: it completes the
    /// queue and waits, so the write is finished rather than probably finished.
    /// </remarks>
    [Fact]
    public async Task UpdateVm_WritesThatOneMachinesStateWithNoPassAtAll()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, PowerState.On, node: Node);

        await h.Service.UpdateVm(await Machine(Vmid, "stopped", OtherNode));
        await h.Service.StopAsync(Ct);

        var vm = await Reread(VmA);
        Assert.Equal(PowerState.Off, vm.PowerState);
        Assert.Equal(OtherNode, vm.ProxmoxVmInfo.Node);
        Assert.Equal(1, h.Loop.Passes);
        Assert.Empty(Failures(h));
    }

    /// <summary>
    /// A machine Player has no row for is looked for and dropped. Reached the same way the poller reaches
    /// it - <c>ResolveNode</c> asks PVE about a vmid, and the row it came from can be deleted between the
    /// two - so this must not be an error.
    /// </summary>
    [Fact]
    public async Task UpdateVm_WithAVmidPlayerDoesNotTrack_DoesNothing()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, PowerState.On);

        await h.Service.UpdateVm(await Machine(UntrackedVmid));
        await h.Service.StopAsync(Ct);

        Assert.Equal(PowerState.On, (await Reread(VmA)).PowerState);
        Assert.Equal(1, h.Loop.Passes);
        Assert.Empty(Failures(h));
    }

    /// <summary>
    /// A null resource is refused before a scope is taken, which is the shape <c>ResolveNode</c> hands
    /// back for a vmid PVE has never heard of. The guard is ahead of the <c>try</c>, and its own catch
    /// would read <c>pveVm.VmId</c> - so without it the queue's only handler throws where nothing can
    /// catch it, which faults the <c>ActionBlock</c> permanently.
    /// </summary>
    [Fact]
    public async Task UpdateVm_WithNothingToUpdate_TakesNoScopeAndDoesNotThrow()
    {
        var h = Build();
        h.Loop.AllowedPasses = 0;

        await h.Service.UpdateVm(null);
        await h.Service.StopAsync(Ct);

        Assert.Equal(0, h.Loop.Passes);
        Assert.Empty(h.Log.At(LogLevel.Error));
    }

    /// <summary>
    /// A queued machine that cannot be processed is logged with its vmid and the queue goes on taking the
    /// next one. Everything inside the handler is inside a <c>try</c> for this reason: an exception that
    /// escaped it would complete the <c>ActionBlock</c> as faulted, and since it is built once in the
    /// constructor, every later <c>UpdateVm</c> for the life of the process would be silently discarded -
    /// with no log entry after the first, because <c>SendAsync</c> to a faulted block just returns false.
    /// </summary>
    /// <remarks>
    /// The failure is <see cref="PollLoop"/> refusing a pass, which is the cheapest way to break exactly
    /// one item: raising the allowance afterwards is what makes the second item's success the assertion
    /// rather than a race with the first.
    /// </remarks>
    [Fact]
    public async Task WhenOneQueuedMachineFails_ItIsLoggedAndTheQueueStillTakesTheNext()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, PowerState.On);
        await SeedVm(VmB, OtherVmid, PowerState.On);

        h.Loop.AllowedPasses = 0;
        await h.Service.UpdateVm(await Machine(Vmid, "stopped"));
        await PollLoop.Until(() => h.Loop.Passes >= 1, "the first queued machine to be refused a scope");

        h.Loop.AllowedPasses = 2;
        await h.Service.UpdateVm(await Machine(OtherVmid, "stopped"));
        await h.Service.StopAsync(Ct);

        var logged = Assert.Single(h.Log.At(LogLevel.Error));
        Assert.Contains($"Exception processing Proxmox vmid={Vmid}", logged.Message);
        Assert.IsType<InvalidOperationException>(logged.Exception);

        Assert.Equal(PowerState.On, (await Reread(VmA)).PowerState);
        Assert.Equal(PowerState.Off, (await Reread(VmB)).PowerState);
    }

    /// <summary>
    /// Stopping while a queued machine is still being written says so rather than waiting for it. The
    /// host gives <c>StopAsync</c> a token that is cancelled after its shutdown timeout, so the choice
    /// here is between a warning and a shutdown that hangs; the warning is also the operator's only
    /// evidence that some machine's state on the way out was dropped.
    /// </summary>
    /// <remarks>
    /// The job is held inside the scope it asked for, so the queue genuinely has work in flight - a
    /// completed queue would satisfy the wait before the cancelled token was ever looked at. It is
    /// released afterwards and drained properly, so nothing is still writing to this test's database
    /// while it is being taken down.
    /// </remarks>
    [Fact]
    public async Task WhenStoppedWhileQueuedWorkIsInFlight_ItSaysSoRatherThanWaiting()
    {
        using var held = new ManualResetEventSlim(false);
        var h = Build(hold: held);
        await SeedVm(VmA, Vmid, PowerState.On);

        await h.Service.UpdateVm(await Machine(Vmid, "stopped"));
        await PollLoop.Until(() => h.Loop.Passes >= 1, "the queued machine to reach its scope");

        await h.Service.StopAsync(new CancellationToken(true));

        var warning = Assert.Single(h.Log.At(LogLevel.Warning));
        Assert.Contains("Stopped before in-flight Proxmox jobs finished", warning.Message);

        held.Set();
        await h.Service.StopAsync(Ct);

        // And the work it did not wait for still landed, which is what "reconciled on next start" means
        // when the process outlives the stop.
        Assert.Equal(PowerState.Off, (await Reread(VmA)).PowerState);
    }

    #endregion

    /// <summary>
    /// Everything one test needs: the service, the harness driving its loop, the cluster it asks, and the
    /// options object a test can edit while it runs.
    /// </summary>
    private sealed record Harness(
        ProxmoxStateService Service,
        PollLoop Loop,
        IProxmoxService Proxmox,
        ProxmoxOptions Options,
        RecordingLogger<ProxmoxStateService> Log)
    {
        /// <summary>What the cluster answers when a pass asks it what it is running.</summary>
        public void Returns(params IClusterResourceVm[] vms) =>
            Proxmox.GetVms().Returns(Task.FromResult<IEnumerable<IClusterResourceVm>>(vms));
    }

    /// <param name="hold">
    /// Blocks each scope until it is set, for the one test that needs a job it can catch in flight.
    /// </param>
    private Harness Build(
        bool enabled = true, int interval = NeverOnItsOwn, ManualResetEventSlim hold = null)
    {
        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetVms().Returns(Task.FromResult<IEnumerable<IClusterResourceVm>>([]));

        // One instance behind CurrentValue rather than a value per call, so a test can edit an option
        // mid-run the way a configuration reload does - which is the only way to reach the code that
        // reads the same option twice and expects a different answer.
        var options = new ProxmoxOptions { Enabled = enabled, StateRefreshIntervalSeconds = interval };
        var monitor = Substitute.For<IOptionsMonitor<ProxmoxOptions>>();
        monitor.CurrentValue.Returns(options);

        var loop = new PollLoop(
            () =>
            {
                hold?.Wait(Ct);

                return NewContext();
            },
            proxmox);

        var log = new RecordingLogger<ProxmoxStateService>();

        return new Harness(new ProxmoxStateService(log, monitor, loop), loop, proxmox, options, log);
    }

    /// <summary>
    /// What <c>/cluster/resources</c> reports, deserialized by the real Proxmox client from the JSON PVE
    /// would have sent - which is the only way to get a resource whose power flags mean anything. See the
    /// class remarks.
    /// </summary>
    /// <param name="machines">A vmid, its status in PVE's vocabulary, and the node it is on.</param>
    private static async Task<IClusterResourceVm[]> Reporting(
        params (int Vmid, string Status, string Node)[] machines)
    {
        var cluster = new FakeProxmoxCluster();

        foreach (var (vmid, status, node) in machines)
        {
            cluster.Has(vmid, status: status, node: node);
        }

        return [.. await cluster.Service().GetVms()];
    }

    private static Task<IClusterResourceVm[]> Reporting(
        int vmid, string status = "running", string node = Node) =>
        Reporting((vmid, status, node));

    /// <summary>One machine, for the <c>UpdateVm</c> path, which takes a resource rather than a list.</summary>
    private static async Task<IClusterResourceVm> Machine(
        int vmid, string status = "running", string node = Node) =>
        (await Reporting(vmid, status, node)).Single();

    /// <summary>
    /// A Proxmox Vm the poller can resolve a vmid to, which takes both the Vm row and the
    /// <c>ProxmoxVmInfo</c> that carries the vmid.
    /// </summary>
    private Task SeedVm(
        Guid vmId,
        int vmid,
        PowerState power = PowerState.Unknown,
        VmType type = VmType.Proxmox,
        string node = Node,
        bool pending = false) =>
        Seed(new VmEntity
        {
            Id = vmId,
            Name = $"proxmox-{vmid}",
            Type = type,
            PowerState = power,
            HasPendingTasks = pending,
            ProxmoxVmInfo = new ProxmoxVmInfo { VmId = vmId, Id = vmid, Node = node },
        });

    /// <summary>A Vm from the other hypervisor, which is a Vm with no <c>ProxmoxVmInfo</c> at all.</summary>
    private Task SeedVsphereVm(Guid vmId, PowerState power) =>
        Seed(new VmEntity
        {
            Id = vmId,
            Name = "vsphere",
            Type = VmType.Vsphere,
            PowerState = power,
        });

    /// <summary>
    /// What the row says now, through a context that never saw the write - the only way to see what a
    /// pass committed rather than what its own change tracker thinks. <c>ProxmoxVmInfo</c> comes with it:
    /// the navigation is <c>AutoInclude</c>, which is also why the poller's own query needs no include.
    /// </summary>
    private async Task<VmEntity> Reread(Guid vmId)
    {
        await using var context = NewContext();

        return await context.Vms.SingleAsync(x => x.Id == vmId, Ct);
    }

    /// <summary>
    /// The errors a pass logged that are its own, rather than <see cref="PollLoop"/> refusing a pass past
    /// its allowance - which the loop's catch records like any other failure.
    /// </summary>
    private static IEnumerable<RecordingLogger.LogEntry> Failures(Harness h) =>
        h.Log.At(LogLevel.Error).Where(x => x.Exception is not InvalidOperationException);

    /// <summary>The complaints about the configured interval, which are the only warnings a pass makes.</summary>
    private static IEnumerable<RecordingLogger.LogEntry> Warnings(Harness h) =>
        h.Log.At(LogLevel.Warning).Where(x => x.Message.Contains("StateRefreshIntervalSeconds"));

    /// <summary>
    /// The <c>EntityUpdated</c> the interceptor published for the save the pass made, so that the
    /// modified property names are the ones EF's change tracker produced.
    /// </summary>
    private EntityUpdated<VmEntity> TheUpdate() =>
        Assert.Single(Mediator.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IMediator.Publish))
            .Select(x => x.GetArguments()[0])
            .OfType<EntityUpdated<VmEntity>>());
}
