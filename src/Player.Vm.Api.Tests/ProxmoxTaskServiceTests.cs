// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Node;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// <c>ProxmoxTaskService</c>, the poller that makes a Proxmox power operation visible in the UI. It reads
/// the cluster's task list, reconciles <c>Vm.HasPendingTasks</c> from it, and pushes a progress
/// notification into the <c>ProgressHub</c> group named after each Vm - so a machine started from the PVE
/// web UI or the <c>qm</c> CLI shows as busy in Player just as one started through this API does.
/// </summary>
/// <remarks>
/// <para>
/// Driven over <see cref="PollLoop"/>, which is what makes a loop with no return value assertable: both
/// poll intervals are configured as a minute so that nothing but the harness's own nudge advances the
/// loop, and a pass is counted by the scope it creates. The exceptions are the two interval tests, which
/// are the only ones here whose subject is the clock.
/// </para>
/// <para>
/// The vSphere counterpart is <c>TaskService</c>, covered by <c>TaskServiceTests</c>, and the pairing is
/// the reason several assertions below look like restatements of the code. Three cross-driver facts are
/// only visible with both pinned. The two pollers share one <c>Notification</c> model - this file uses
/// <c>Domain.Vsphere.Models.Notification</c> because that is what the Proxmox poller builds - but they do
/// not share its vocabulary: <c>state</c> is <c>"running"</c> or PVE's own status string here, against
/// vSphere's <c>"queued"</c>, <c>"running"</c> and <c>"success"</c>. <c>progress</c> is permanently the
/// empty string here, because PVE's cluster task list carries no percentage, where vSphere fills it from
/// <c>info.progress</c>. And each poller sweeps only its own provider's Vms, because whichever ran last
/// would otherwise clear the other's flags - the comment saying so is on vSphere's query, at
/// <c>TaskService.cs:122</c>, and the matching filter here is at <c>ProxmoxTaskService.cs:178</c>.
/// </para>
/// <para>
/// A task is "still running" here because its <c>Duration</c> is null, which the poller reads at
/// <c>ProxmoxTaskService.cs:159</c>. <c>Duration</c> cannot be arranged: on <c>NodeTask</c> it is computed,
/// and it is null exactly while <c>EndTime</c> is zero - so these tests set <c>StartTime</c> and
/// <c>EndTime</c>, which is what the client maps the presence or absence of PVE's <c>endtime</c> key to.
/// The same fact one layer up is pinned end to end by
/// <c>ProxmoxServiceCommandTests.GetTasks_ATaskWithNoEndTimeHasNoDuration_WhichIsHowStillRunningIsSpelled</c>.
/// </para>
/// <para>
/// <c>Description</c> is computed too, from <c>Type</c> and <c>VmId</c> - <c>qmstart</c> on vmid 100 is
/// "VM 100 Start", <c>vncproxy</c> is "VM/CT 100 Console", and a type the client's table does not know
/// falls through to the raw string. So the <c>taskName</c> a client is shown is the Proxmox client's own
/// sentence rather than anything this application composes.
/// </para>
/// </remarks>
public class ProxmoxTaskServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>
    /// A minute, so that only the harness's nudge can turn the loop. See <see cref="PollLoop"/>: if the
    /// interval were what advanced it, every test here would be a race rather than an assertion.
    /// </summary>
    private const int NeverOnItsOwn = 60_000;

    /// <summary>Short enough that a pass the poller decides to take arrives well inside a test.</summary>
    private const int AtOnce = 25;

    private const int Vmid = 100;
    private const int OtherVmid = 101;

    private const string StartUpid = "UPID:pve1:0000ABCD:0011:0022:qmstart:100:player@pve!vmapi:";
    private const string StopUpid = "UPID:pve1:0000ABCE:0011:0033:qmstop:101:player@pve!vmapi:";

    private static readonly Guid VmA = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid VmB = new("bbbbbbbb-0000-4000-8000-000000000002");

    /// <summary>An arbitrary unix start time, so that a finished task's duration is a real five seconds.</summary>
    private const long StartedAt = 1_700_000_000;

    #region The enabled switch

    /// <summary>
    /// With <c>Proxmox:Enabled</c> false the pass does nothing at all - it does not even create a scope,
    /// so no database is read and no cluster is dialed. That is what lets a deployment with no Proxmox
    /// cluster register this service without it failing on every turn.
    /// </summary>
    [Fact]
    public async Task WhenProxmoxIsDisabled_APassCreatesNoScopeAndAsksTheClusterNothing()
    {
        // The gate is inside the loop and ahead of CreateScope, so PollLoop's refusal barrier never
        // trips and there is no effect to wait *for* - only one to wait out. Both intervals are set to
        // 25ms rather than a minute, so by the end of this bounded wait the loop has turned on its own
        // many times over: a gate that let anything through would have created a scope long before it
        // elapsed, and one nudge on top of that makes the first turn immediate.
        var h = Build(enabled: false, check: AtOnce, recheck: AtOnce);
        await SeedVm(VmA, Vmid, pending: true);
        h.Returns(Running("100"));

        await h.Service.StartAsync(Ct);
        h.Service.CheckTasks();
        await Task.Delay(250, Ct);
        await h.Service.StopAsync(Ct);

        Assert.Equal(0, h.Loop.Passes);
        await h.Proxmox.DidNotReceive().GetTasks();
        Assert.Empty(h.Hub.Sends);

        // And the flag it would have cleared is untouched, which is what a disabled poller looks like
        // from the UI: whatever the last enabled pass decided stands.
        Assert.True(await Flagged(VmA));
    }

    #endregion

    #region Reconciling HasPendingTasks

    /// <summary>
    /// A task PVE is still working on flags its Vm, read back through a context that never saw the write.
    /// This is the whole reason the poller exists: the flag is what greys the power buttons out.
    /// </summary>
    [Fact]
    public async Task ARunningTask_FlagsItsVmAsHavingPendingWork()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(Running("100"));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.True(await Flagged(VmA));
    }

    // A task PVE has finished is what clears the flag again. Nothing else does - IProxmoxService submits
    // power operations without handing off the UPID, so this poller is the only thing that can.
    [Fact]
    public async Task AFinishedTask_ClearsAVmThatWasFlagged()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, pending: true);
        h.Returns(Finished("100"));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.False(await Flagged(VmA));
    }

    // The pass rewrites every flag from scratch, so a Vm whose task is still going has to survive its own
    // reconciliation: a clear-then-set that got the order wrong would flicker the UI on every pass.
    [Fact]
    public async Task AFlaggedVmWhoseTaskIsStillRunning_StaysFlagged()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, pending: true);
        h.Returns(Running("100"));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.True(await Flagged(VmA));
    }

    /// <summary>
    /// An idle cluster clears everything and tells nobody. This is the state a Vm has to end up in after
    /// an API restart: nothing replays the tasks that were running, so a flag left set by the pass before
    /// the restart would stick until something else happened to that machine.
    /// </summary>
    [Fact]
    public async Task WithNoTasksAtAll_AFlaggedVmIsClearedAndNothingIsBroadcast()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, pending: true);

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.False(await Flagged(VmA));
        Assert.Empty(h.Hub.Sends);
    }

    /// <summary>
    /// The sweep is Proxmox-only: a flagged vSphere Vm keeps its flag. The two pollers reconcile the same
    /// column, so whichever ran last would otherwise clear the other's work - and this API can be
    /// deployed with only one of the two configured, in which case the idle poller would clear every
    /// flag on every pass.
    /// </summary>
    /// <remarks>
    /// The other half of the pairing is vSphere's <c>Where(x =&gt; x.HasPendingTasks &amp;&amp; x.Type !=
    /// VmType.Proxmox)</c> at <c>TaskService.cs:127</c>, which carries the comment explaining it;
    /// <c>TaskServiceTests</c> covers that side.
    /// </remarks>
    [Fact]
    public async Task AFlaggedVsphereVm_IsLeftAloneBecauseTheOtherPollerOwnsIt()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, pending: true);
        await Seed(new VmEntity
        {
            Id = VmB,
            Name = "vsphere-vm",
            Type = VmType.Vsphere,
            HasPendingTasks = true,
        });

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.False(await Flagged(VmA));
        Assert.True(await Flagged(VmB));
    }

    /// <summary>
    /// The asymmetry in that filter, characterized rather than asserted as intent: the query that
    /// <em>sets</em> the flag has no provider filter, so a Vm with Proxmox info but some other
    /// <c>Vm.Type</c> can be flagged by this poller and then never cleared by it - the clearing sweep
    /// filters on the type and skips it forever.
    /// </summary>
    /// <remarks>
    /// A trap door rather than a live bug, because nothing in the application writes a
    /// <c>ProxmoxVmInfo</c> for a Vm it did not also type as Proxmox. It is reachable by a mis-migration
    /// or by hand-edited data, and what a user sees is a machine whose power buttons never come back.
    /// The fix is the same <c>Type == VmType.Proxmox</c> clause on the second query, at
    /// <c>ProxmoxTaskService.cs:193</c>. This test turns red when that is added, and the assertion to
    /// keep then is that the Vm was never flagged in the first place.
    /// </remarks>
    [Fact]
    public async Task AVmWithProxmoxInfoButAnotherType_IsFlaggedAndThenNeverCleared()
    {
        var h = Build();
        await Seed(new VmEntity
        {
            Id = VmA,
            Name = "mistyped",
            Type = VmType.Unknown,
            ProxmoxVmInfo = new ProxmoxVmInfo { VmId = VmA, Id = Vmid, Node = "pve1" },
        });

        h.Proxmox.GetTasks().Returns(
            Task.FromResult<IEnumerable<NodeTask>>([Running("100")]),
            Task.FromResult<IEnumerable<NodeTask>>([]));

        await h.Loop.Run(h.Service, h.Service.CheckTasks, passes: 2);

        Assert.True(await Flagged(VmA));
    }

    #endregion

    #region The notification payload

    /// <summary>
    /// What a subscribed client is handed for a task that is still going. The four fields taken off the
    /// task are the whole message - the client has no other source for what is happening to the machine -
    /// and <c>taskName</c> is the human sentence the Proxmox client derives from the task's type and vmid,
    /// which is the only reason a UI can label this "VM 100 Start" rather than "qmstart".
    /// </summary>
    [Fact]
    public async Task ARunningTasksNotification_CarriesTheUpidTheDescriptionAndTheTypeAndReadsAsRunning()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(Running("100", type: "qmstart"));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        var notification = Assert.Single(Notifications(h, VmA));
        Assert.Equal(StartUpid, notification.taskId);
        Assert.Equal("VM 100 Start", notification.taskName);
        Assert.Equal("qmstart", notification.taskType);
        Assert.Equal("running", notification.state);

        // Permanently empty, because PVE's cluster task list has no percentage in it. vSphere's poller
        // fills the same field from info.progress, so a client rendering a progress bar off this shows
        // one for a vSphere machine and nothing for a Proxmox one.
        Assert.Equal(string.Empty, notification.progress);
    }

    /// <summary>
    /// Once a task has finished, <c>state</c> is whatever PVE called it, passed through verbatim rather
    /// than folded into a vocabulary of this application's own - so a failure reaches the client as the
    /// cluster's own sentence and not as a generic "failed".
    /// </summary>
    [Theory]
    [InlineData("OK")]
    [InlineData("start failed: QEMU exited with code 1")]
    [InlineData("interrupted by signal")]
    public async Task AFinishedTasksNotification_ReportsProxmoxsOwnStatusAsTheState(string status)
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(Finished("100", status: status));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        var notification = Assert.Single(Notifications(h, VmA));
        Assert.Equal(status, notification.state);
        Assert.Equal(string.Empty, notification.progress);
    }

    #endregion

    #region Who the broadcast is addressed to

    /// <summary>
    /// The group is named by the Player <c>Vm.Id</c> guid, never by the Proxmox vmid. That string is the
    /// entire contract with a client: <c>ProgressHub.Join</c> takes the name it is given verbatim, and the
    /// Angular console joins <c>vmId.ToString()</c>, so a broadcast to any other name is silently
    /// delivered to nobody.
    /// </summary>
    /// <remarks>
    /// The subscribing half is <c>ProgressHubTests</c>, and neither end compares itself to the other -
    /// which is why this is asserted as a literal here.
    /// </remarks>
    [Fact]
    public async Task TheBroadcastIsAddressedToThePlayerVmId_NotToTheProxmoxVmid()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(Running("100"));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.Equal([VmA.ToString()], h.Hub.Recipients("Progress"));
        Assert.DoesNotContain(Vmid.ToString(), h.Hub.Recipients("Progress"));
    }

    // Two tasks against one machine are one message carrying both, not two messages. PVE reports a
    // shutdown and the stop that follows it as separate tasks, so this is the ordinary case for a power
    // off - and a client that replaced its list on each message would lose one of them.
    [Fact]
    public async Task TwoTasksForOneVm_AreOneBroadcastCarryingBoth()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(
            Running("100", type: "qmshutdown", upid: StartUpid),
            Finished("100", type: "qmstop", upid: StopUpid));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.Single(h.Hub.Of("Progress"));
        Assert.Equal(
            [StartUpid, StopUpid],
            Notifications(h, VmA).Select(x => x.taskId).Order());
    }

    /// <summary>
    /// Tasks for two machines are one message per group, each carrying only its own machine's tasks. A
    /// client subscribed to one Vm must not learn what is being done to another, and the grouping is the
    /// only thing that stops it.
    /// </summary>
    /// <remarks>
    /// The dictionary the poller iterates is a <c>ConcurrentDictionary</c>, so which group is addressed
    /// first is not defined; these assertions are per group rather than an ordered list of sends.
    /// </remarks>
    [Fact]
    public async Task TasksForTwoVms_AreOneBroadcastEachCarryingOnlyItsOwn()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        await SeedVm(VmB, OtherVmid);
        h.Returns(
            Running("100", upid: StartUpid),
            Running("101", upid: StopUpid));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.Equal(2, h.Hub.Of("Progress").Count);
        Assert.Equal([VmA.ToString(), VmB.ToString()], h.Hub.Recipients("Progress").Order());
        Assert.Equal(StartUpid, Assert.Single(Notifications(h, VmA)).taskId);
        Assert.Equal(StopUpid, Assert.Single(Notifications(h, VmB)).taskId);
    }

    #endregion

    #region Tasks that are skipped

    /// <summary>
    /// A console session is skipped entirely - no notification and no pending flag. PVE models an
    /// interactive console as a task that stays running for as long as the session is open, so counting
    /// one as pending work would leave every Vm with an open console flagged forever <em>and</em> hold
    /// the poller at its fast ReCheck interval permanently, polling the cluster every second for the life
    /// of the session.
    /// </summary>
    /// <remarks>
    /// The list is at <c>ProxmoxTaskService.cs:54</c> and its doc comment is where that reasoning is
    /// written down. vSphere needs no equivalent, because vCenter does not report console sessions as
    /// recent tasks at all - so this is one of the two places the drivers differ in kind rather than in
    /// wording.
    /// </remarks>
    [Theory]
    [InlineData("vncproxy")]
    [InlineData("spiceproxy")]
    [InlineData("termproxy")]
    [InlineData("vncshell")]
    [InlineData("spiceshell")]
    [InlineData("VNCproxy")]
    public async Task ARunningConsoleSessionTask_IsNeitherBroadcastNorCountedAsPendingWork(string type)
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(Running("100", type: type));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.Empty(h.Hub.Sends);
        Assert.False(await Flagged(VmA));
    }

    /// <summary>
    /// <c>NodeTask.VmId</c> is PVE's generic entity id rather than a vmid, and a task against something
    /// that is not a VM at all carries something else in it - a backup task reports its storage, e.g.
    /// "local:backup". Anything that does not parse as an integer is dropped rather than reaching the
    /// dictionary lookup.
    /// </summary>
    [Theory]
    [InlineData("local:backup")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("100.5")]
    public async Task ATaskWhoseEntityIdIsNotAVmid_IsSkippedWithNothingLogged(string entityId)
    {
        var h = Build();
        await SeedVm(VmA, Vmid, pending: true);
        h.Returns(Running(entityId));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.Empty(h.Hub.Sends);
        Assert.False(await Flagged(VmA));

        // Dropped as an ordinary outcome, not as a failure: these arrive on every pass of a cluster that
        // runs backups, and logging one per pass would be the loudest thing in the log.
        Assert.DoesNotContain(h.Log.At(LogLevel.Error), x => x.Exception is not InvalidOperationException);
    }

    // A vmid Player does not track is dropped the same way. The cluster task list is cluster-wide, so
    // every machine on the hypervisor that Player knows nothing about is in it.
    [Fact]
    public async Task ATaskForAVmidPlayerDoesNotTrack_IsSkipped()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, pending: true);
        h.Returns(Running("999"));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.Empty(h.Hub.Sends);
        Assert.False(await Flagged(VmA));
    }

    /// <summary>
    /// One bad task does not cost the pass the rest of them. The per-task <c>try</c> is what makes that
    /// true, and a null entry in the list is the cheapest way to reach it - the flag for the good task is
    /// still written and the failure is logged with the task's own id, which is null here for the same
    /// reason the entry was.
    /// </summary>
    [Fact]
    public async Task ATaskTheServiceCannotReadAtAll_IsLoggedAndTheRestOfThePassStillLands()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        h.Returns(null, Running("100"));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.True(await Flagged(VmA));
        Assert.Equal([VmA.ToString()], h.Hub.Recipients("Progress"));

        var logged = Assert.Single(h.Log.At(LogLevel.Error), x => x.Exception is NullReferenceException);
        Assert.Contains("Exception processing Proxmox task", logged.Message);
    }

    #endregion

    #region Which interval the next pass comes on

    /// <summary>
    /// With something still running, the next pass comes on <c>ReCheckTaskProgressIntervalMilliseconds</c>.
    /// That is what makes a power operation look responsive: while a task is in flight the poller runs at
    /// a second rather than at five, and drops back once the cluster is idle.
    /// </summary>
    /// <remarks>
    /// Nothing nudges the loop here - see <see cref="PollLoop.RunUnprompted"/> - so a second pass
    /// arriving at all can only be the pending arm of the ternary choosing 25ms over the minute beside
    /// it. Swapping the two arms fails this by timing out rather than by a hair.
    /// </remarks>
    [Fact]
    public async Task WithATaskStillRunning_TheNextPassComesOnTheReCheckInterval()
    {
        var h = Build(check: NeverOnItsOwn, recheck: AtOnce);
        await SeedVm(VmA, Vmid);
        h.Returns(Running("100"));

        await h.Loop.RunUnprompted(h.Service, h.Service.CheckTasks);

        Assert.True(h.Loop.Passes >= 2);
    }

    /// <summary>
    /// The other arm: with nothing running, the next pass comes on
    /// <c>CheckTaskProgressIntervalMilliseconds</c>. Read against the test above - between them the two
    /// say the choice is made rather than that one interval happens to be used for everything.
    /// </summary>
    [Fact]
    public async Task WithNothingStillRunning_TheNextPassComesOnThePlainCheckInterval()
    {
        var h = Build(check: AtOnce, recheck: NeverOnItsOwn);
        await SeedVm(VmA, Vmid);
        h.Returns(Finished("100"));

        await h.Loop.RunUnprompted(h.Service, h.Service.CheckTasks);

        Assert.True(h.Loop.Passes >= 2);
    }

    /// <summary>
    /// <c>CheckTasks</c> brings the next poll forward instead of waiting the interval out. This is what
    /// makes a power command show as pending immediately: <c>CheckProxmoxTasksBehavior</c> pokes the
    /// poller once the command has been submitted, so the UI does not sit on a stale unflagged Vm for up
    /// to five seconds.
    /// </summary>
    /// <remarks>
    /// Every other test in this class depends on it through <see cref="PollLoop.Run"/>, which is exactly
    /// why it is worth one test of its own: a class whose whole harness stops working is not a class that
    /// reports a broken <c>CheckTasks</c> clearly.
    /// </remarks>
    [Fact]
    public async Task CheckTasks_BringsTheNextPassForwardRatherThanWaitingOutTheInterval()
    {
        var h = Build();
        h.Loop.AllowedPasses = 2;

        await h.Service.StartAsync(Ct);

        try
        {
            await PollLoop.Until(() => h.Loop.Passes >= 1, "the first pass");

            // Both intervals are a minute, so the loop is now asleep and no second pass can arrive on
            // its own inside the bounded wait below.
            h.Service.CheckTasks();
            await PollLoop.Until(() => h.Loop.Passes >= 2, "a second pass after CheckTasks");
        }
        finally
        {
            await h.Service.StopAsync(Ct);
        }

        await h.Proxmox.Received(2).GetTasks();
    }

    #endregion

    #region Failures the loop swallows

    /// <summary>
    /// A cluster that cannot be reached is logged at Error with the exception and costs the pass, and the
    /// loop keeps turning. That is the difference between a Proxmox outage and a dead poller: without the
    /// <c>catch</c>, the first failure would fault the execute task and no Vm's flag would ever move
    /// again until the API was restarted.
    /// </summary>
    [Fact]
    public async Task WhenTheClusterCannotBeReached_TheFailureIsLoggedAndTheLoopKeepsTurning()
    {
        var h = Build();
        await SeedVm(VmA, Vmid, pending: true);
        h.Proxmox.GetTasks().Returns<Task<IEnumerable<NodeTask>>>(
            _ => throw new HttpRequestException("no route to host"));

        await h.Loop.Run(h.Service, h.Service.CheckTasks, passes: 2);

        // Two passes, so the second one is the evidence the first did not stop the loop.
        await h.Proxmox.Received(2).GetTasks();

        var errors = h.Log.At(LogLevel.Error).Where(x => x.Exception is HttpRequestException).ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, x => Assert.Contains("Exception in ProxmoxTaskService", x.Message));

        // Nothing was reconciled, which is the right answer to "the cluster did not say": a flag cleared
        // on a failed read would report a busy machine as idle.
        Assert.True(await Flagged(VmA));
    }

    /// <summary>
    /// A broadcast that fails for one group is logged and the other group is still told. Every message
    /// this poller sends goes to a different set of connections, so one dead client must not cost the
    /// rest of a pass its notification.
    /// </summary>
    [Fact]
    public async Task WhenOneGroupsBroadcastThrows_ItIsLoggedAndTheOtherGroupIsStillTold()
    {
        var h = Build();
        await SeedVm(VmA, Vmid);
        await SeedVm(VmB, OtherVmid);
        h.Returns(Running("100", upid: StartUpid), Running("101", upid: StopUpid));

        h.Hub.FailsFor(VmA.ToString(), new IOException("the connection went away"));

        await h.Loop.Run(h.Service, h.Service.CheckTasks);

        Assert.Equal([VmB.ToString()], h.Hub.Recipients("Progress"));
        Assert.Equal(StopUpid, Assert.Single(Notifications(h, VmB)).taskId);

        var logged = Assert.Single(h.Log.At(LogLevel.Error), x => x.Exception is IOException);
        Assert.Contains(VmA.ToString(), logged.Message);

        // The database side of the pass is finished before any broadcast is attempted, so both Vms are
        // flagged whatever the hub does with the message.
        Assert.True(await Flagged(VmA));
        Assert.True(await Flagged(VmB));
    }

    #endregion

    /// <summary>
    /// Everything one test needs: the service, the harness driving its loop, and the three collaborators
    /// assertions are made against.
    /// </summary>
    private sealed record Harness(
        ProxmoxTaskService Service,
        PollLoop Loop,
        IProxmoxService Proxmox,
        HubContextHarness<ProgressHub> Hub,
        RecordingLogger<ProxmoxTaskService> Log)
    {
        /// <summary>What the cluster answers when the pass asks for its task list.</summary>
        public void Returns(params NodeTask[] tasks) =>
            Proxmox.GetTasks().Returns(Task.FromResult<IEnumerable<NodeTask>>(tasks));
    }

    private Harness Build(bool enabled = true, int check = NeverOnItsOwn, int recheck = NeverOnItsOwn)
    {
        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetTasks().Returns(Task.FromResult<IEnumerable<NodeTask>>([]));

        var options = Substitute.For<IOptionsMonitor<ProxmoxOptions>>();
        options.CurrentValue.Returns(new ProxmoxOptions
        {
            Enabled = enabled,
            CheckTaskProgressIntervalMilliseconds = check,
            ReCheckTaskProgressIntervalMilliseconds = recheck,
        });

        var loop = new PollLoop(NewContext, proxmox);
        var hub = new HubContextHarness<ProgressHub>();
        var log = new RecordingLogger<ProxmoxTaskService>();

        return new Harness(
            new ProxmoxTaskService(log, options, loop, hub.Context), loop, proxmox, hub, log);
    }

    /// <summary>
    /// A task PVE has not finished, which it spells as the absence of an end time: <c>EndTime</c> left at
    /// zero, which is what the client maps a JSON payload with no <c>endtime</c> key to.
    /// </summary>
    private static NodeTask Running(string entityId, string type = "qmstart", string upid = StartUpid) =>
        new()
        {
            UniqueTaskId = upid,
            VmId = entityId,
            Type = type,
            Node = "pve1",
            StartTime = StartedAt,
        };

    /// <summary>
    /// The same task once it has finished: an end time, which is what gives it a <c>Duration</c>, and a
    /// status to report as the state.
    /// </summary>
    private static NodeTask Finished(
        string entityId,
        string status = "OK",
        string type = "qmstart",
        string upid = StartUpid)
    {
        var task = Running(entityId, type, upid);
        task.Status = status;
        task.EndTime = StartedAt + 5;

        return task;
    }

    /// <summary>
    /// A Proxmox Vm the poller can resolve a vmid to, which takes both the Vm row and the
    /// <c>ProxmoxVmInfo</c> that carries the vmid.
    /// </summary>
    private Task SeedVm(Guid vmId, int vmid, bool pending = false) =>
        Seed(new VmEntity
        {
            Id = vmId,
            Name = $"proxmox-{vmid}",
            Type = VmType.Proxmox,
            HasPendingTasks = pending,
            ProxmoxVmInfo = new ProxmoxVmInfo { VmId = vmId, Id = vmid, Node = "pve1" },
        });

    /// <summary>
    /// What the row says now, through a context that never saw the write - the only way to see what a
    /// pass committed rather than what its own change tracker thinks.
    /// </summary>
    private async Task<bool> Flagged(Guid vmId)
    {
        await using var context = NewContext();

        return (await context.Vms.SingleAsync(x => x.Id == vmId, Ct)).HasPendingTasks;
    }

    /// <summary>The notifications sent to one Vm's group, as the client would receive them.</summary>
    private static List<Notification> Notifications(Harness h, Guid vmId) =>
        (List<Notification>)h.Hub.Of("Progress")
            .Single(x => x.Groups.Contains(vmId.ToString()))
            .Args[0];
}
