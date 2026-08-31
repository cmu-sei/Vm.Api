// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services.HealthChecks;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Tests.Infrastructure;
using VimClient;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// vSphere's <c>TaskService</c>: the poller that reads vCenter's recent-task list, reconciles
/// <c>Vm.HasPendingTasks</c>, broadcasts progress into <c>ProgressHub</c> groups and pokes
/// <c>IMachineStateService</c> when a power task finishes. It is what drives the progress bar and the
/// spinner in the VM UI, and it is the reason a machine stops looking busy once vCenter is done with it.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="PollLoop"/>, which is the service provider the loop resolves its scope from
/// and so the only thing that makes a pass countable - read its docs before adding a test here. Both poll
/// intervals are configured at a minute so that nothing but the harness's nudge advances the loop; the
/// tests in the last region are the exceptions, because the interval is what they are about.
/// </para>
/// <para>
/// The seam below the service is a substituted <see cref="IVimClient"/> per vCenter, built the way
/// <c>VsphereServiceCommandTests.FakeVcenter</c> builds one, so no vCenter is involved and the property
/// collector's answer is whatever a test says it is. <c>IConnectionService</c> stands in for the moref
/// cache a live connection would have loaded.
/// </para>
/// <para>
/// The Proxmox mirror of the first region is <c>ProxmoxTaskServiceTests</c>. The two pollers reconcile the
/// same column for different providers and each excludes the other's machines, so neither exclusion is
/// asserted by anything but its own class - and a green run of one says nothing about the other.
/// </para>
/// </remarks>
public class TaskServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>
    /// A minute, so that only <see cref="PollLoop.Run"/>'s nudge can turn the loop. The interval tests
    /// use this for the arm they expect <em>not</em> to be chosen, which is what makes the margin between
    /// passing and failing four orders of magnitude rather than a hair.
    /// </summary>
    private const int NeverOnItsOwn = 60_000;

    /// <summary>The two task types <c>GetPowerTaskTypes</c> lists, which are the ones that move a power state.</summary>
    private const string PowerOn = "VirtualMachine.powerOn";

    private const string PowerOff = "VirtualMachine.powerOff";

    /// <summary>The method name a client listens on. The Angular client's subscription is the other end.</summary>
    private const string Progress = "Progress";

    private static readonly Guid VmA = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid VmB = new("bbbbbbbb-0000-4000-8000-000000000002");

    #region The sweep: which Vms and which connections it covers

    /// <summary>
    /// A Proxmox Vm with tasks outstanding keeps its flag when this poller sweeps, even though no vSphere
    /// connection reported anything about it.
    /// </summary>
    /// <remarks>
    /// The highest-value test in this class, and the reason for the <c>x.Type != VmType.Proxmox</c> clause
    /// at <c>TaskService.cs:127</c> and the comment above it. The two pollers reconcile one column for two
    /// providers, so without the exclusion whichever ran last would clear the other's flags - and this is
    /// the worst arrangement of that, because an install with no vCenter configured has this poller
    /// running, finding no connections at all, and clearing every pending flag in the database on a five
    /// second cycle. What a user would see is the spinner on a Proxmox machine flickering off while the
    /// task is still running. <c>ProxmoxTaskServiceTests</c> covers the mirror image.
    /// </remarks>
    [Fact]
    public async Task AProxmoxVmWithTasksOutstanding_IsLeftAloneByAPassWithNoConnections()
    {
        await Seed(new VmEntity { Id = VmA, Name = "proxmox-vm", Type = VmType.Proxmox, HasPendingTasks = true });
        var poller = Poll();

        await poller.Run();

        Assert.True(await Pending(VmA));
    }

    // The other half of the same pass: a vSphere Vm flagged as busy, with nothing in any recent-task list
    // to keep it that way, is cleared. This is what makes the spinner go away, and it is also what would
    // clear a Proxmox Vm's flag if the type filter were dropped.
    [Fact]
    public async Task AVsphereVmWithNoTaskOfItsOwn_IsCleared()
    {
        await Seed(VsphereVm(VmA, pending: true));
        var poller = Poll();

        await poller.Run();

        Assert.False(await Pending(VmA));
    }

    // A vCenter an operator has switched off in configuration is not asked anything. The connection
    // object still exists and still holds a client, so nothing but this check stops the poller dialing a
    // host that was deliberately taken out of service.
    [Fact]
    public async Task ADisabledConnection_IsNotQueried()
    {
        var vcenter = new Vcenter();
        vcenter.Connection.Host.Enabled = false;
        vcenter.Doing(VmA);
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.Empty(vcenter.Filters);
        Assert.Empty(poller.Hub.Sends);
        Assert.Empty(poller.Errors);
    }

    /// <summary>
    /// A connection that is up but has not finished connecting yet is skipped rather than dereferenced.
    /// </summary>
    /// <remarks>
    /// <c>ConnectionService</c> creates the <c>VsphereConnection</c> before it logs in, so a poll landing
    /// in that window sees a connection with no <c>ServiceContent</c>. Reading
    /// <c>connection.Sic.taskManager</c> there would throw inside the loop over connections, which is
    /// outside every inner <c>try</c> in <c>getRecentTasks</c> - so one vCenter still starting up would
    /// cost the whole pass, including the reconciliation of every other vCenter's machines.
    /// </remarks>
    [Fact]
    public async Task AConnectionStillWaitingForItsServiceContent_IsNotQueried()
    {
        var vcenter = new Vcenter();
        vcenter.Connection.Sic = null;
        await Seed(VsphereVm(VmA, pending: true));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.Empty(vcenter.Filters);
        Assert.Empty(poller.Errors);

        // The pass still finished its own work rather than dying on the way in.
        Assert.False(await Pending(VmA));
    }

    // The same window seen from the other field: Props is the property collector every query is addressed
    // to, and it is assigned alongside Sic on connect and cleared alongside it on disconnect.
    [Fact]
    public async Task AConnectionWithNoPropertyCollector_IsNotQueried()
    {
        var vcenter = new Vcenter();
        vcenter.Connection.Props = null;
        await Seed(VsphereVm(VmA, pending: true));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.Empty(vcenter.Filters);
        Assert.Empty(poller.Errors);
        Assert.False(await Pending(VmA));
    }

    #endregion

    #region The pending flag

    // A machine vCenter is working on is flagged busy even though nothing had flagged it before. The
    // command handlers set the flag when they submit, but a task started from the vSphere client or by
    // vCenter itself reaches the UI only through this.
    [Fact]
    public async Task ARunningTask_FlagsItsVmAsPending()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.running);
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.True(await Pending(VmA));
    }

    // Queued counts as busy too: vCenter serializes work per machine, so a task waiting its turn is one
    // the user is still waiting on.
    [Fact]
    public async Task AQueuedTask_FlagsItsVmAsPending()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.queued);
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.True(await Pending(VmA));
    }

    // A finished task is still in vCenter's recent-task list for minutes afterwards, so "has a task" is
    // not what clears the flag - only a task in a state that is neither queued nor running does.
    [Fact]
    public async Task AVmWhoseOnlyTaskHasFinished_IsCleared()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.success);
        await Seed(VsphereVm(VmA, pending: true));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.False(await Pending(VmA));
    }

    // The reconciliation is per machine and not per pass: one machine still working does not hold the
    // flag on another whose work is done, which is the case a multi-select power on produces every time.
    [Fact]
    public async Task ARunningTaskForOneVm_DoesNotHoldTheFlagOnAnother()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.running);
        vcenter.Doing(VmB, state: TaskInfoState.success);
        await Seed(VsphereVm(VmA, pending: true), VsphereVm(VmB, pending: true));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.True(await Pending(VmA));
        Assert.False(await Pending(VmB));
    }

    #endregion

    #region The notification

    // What a client is actually handed. Every field is read straight off the task except the name, and a
    // notification the UI cannot read is indistinguishable from no progress at all.
    [Fact]
    public async Task TheNotification_CarriesTheKeyTypeProgressAndStateOfTheTask()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.running, type: PowerOn, key: "task-42", progress: 30);
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        var notification = Assert.Single(Notifications(poller, VmA));
        Assert.Equal("task-42", notification.taskId);
        Assert.Equal(PowerOn, notification.taskType);
        Assert.Equal("30", notification.progress);
        Assert.Equal("running", notification.state);
    }

    /// <summary>
    /// <c>taskName</c> is the description id with the <c>"VirtualMachine."</c> prefix stripped, and that
    /// is the string the UI shows a user - so the prefix is a contract rather than a tidy-up.
    /// </summary>
    /// <remarks>
    /// The strip is an unanchored <c>Replace</c> over the whole string rather than a prefix removal, so a
    /// type belonging to any other managed object arrives unchanged. Both rows are here because the
    /// second is what says the substitution is scoped to the one prefix.
    /// </remarks>
    [Theory]
    [InlineData(PowerOn, "powerOn")]
    [InlineData(PowerOff, "powerOff")]
    [InlineData("Datastore.deleteFile", "Datastore.deleteFile")]
    public async Task TheTaskName_IsTheTypeWithoutItsVirtualMachinePrefix(string type, string expected)
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, type: type);
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.Equal(expected, Assert.Single(Notifications(poller, VmA)).taskName);
    }

    /// <summary>
    /// A task carrying nothing but its entity and its state still produces a notification, with the
    /// missing fields as empty strings.
    /// </summary>
    /// <remarks>
    /// Not hypothetical: vCenter omits <c>info.progress</c> for a task that has not started, so this is
    /// what every queued task looks like. The per-task <c>try</c> would log and drop the notification if
    /// the null checks were not there, which for a queued task means the UI learns nothing until the task
    /// starts running.
    /// </remarks>
    [Fact]
    public async Task ATaskMissingItsOptionalProperties_StillProducesANotification()
    {
        var vcenter = new Vcenter();
        vcenter.Knows(VmA);
        vcenter.Reports(new ObjectContent
        {
            propSet =
            [
                new DynamicProperty { name = "info.entity", val = Mor(Moref(VmA)) },
                new DynamicProperty { name = "info.state", val = TaskInfoState.queued },
            ]
        });
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        var notification = Assert.Single(Notifications(poller, VmA));
        Assert.Equal(string.Empty, notification.taskId);
        Assert.Equal(string.Empty, notification.taskType);
        Assert.Equal(string.Empty, notification.taskName);
        Assert.Equal(string.Empty, notification.progress);
        Assert.Equal("queued", notification.state);
        Assert.Empty(poller.Errors);
    }

    /// <summary>
    /// A task that has left vCenter's recent-task list is not broadcast again by the pass after it.
    /// </summary>
    /// <remarks>
    /// <c>_runningTasks</c> is a field on the service and is rebuilt rather than appended to, so the clear
    /// at <c>TaskService.cs:154</c> is the only thing that ends a broadcast. Without it every task the
    /// service ever saw would be re-sent to its group on every pass for the lifetime of the process - a
    /// progress bar frozen at whatever percentage the task was on when it finished, growing by one stale
    /// entry per task, and no way for a client to tell that from real progress.
    /// </remarks>
    [Fact]
    public async Task ATaskThatHasLeftTheRecentList_IsNotBroadcastAgainByTheNextPass()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.running);
        vcenter.OnlyOnce();
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        // Two passes, of which the second finds an empty recent-task list. PollLoop refuses the third,
        // which is what says the second finished - broadcasts included, since they happen in its scope.
        await poller.Run(passes: 2);

        Assert.Single(poller.Hub.Of(Progress));
        Assert.Empty(poller.Errors);
    }

    // The group name is the Player Vm id, not the vCenter moref: that guid is what a client passes to
    // ProgressHub.Join, and ProgressHubTests pins that the hub uses the string it was given verbatim. A
    // name computed any other way here would leave both ends working and no message arriving.
    [Fact]
    public async Task TheBroadcast_GoesToTheGroupNamedByThePlayerVmId()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA);
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.Equal<string>([VmA.ToString()], poller.Hub.Recipients(Progress));
    }

    // Two tasks against one machine arrive as one message carrying both, because the payload is the whole
    // list for that machine. A client that replaced its state from each message would otherwise see only
    // whichever arrived last.
    [Fact]
    public async Task TwoTasksForOneVm_ArriveInOneBroadcastCarryingBoth()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, key: "task-1", type: PowerOn);
        vcenter.Doing(VmA, key: "task-2", type: "VirtualMachine.reconfigure");
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.Single(poller.Hub.Of(Progress));
        Assert.Equal<string>(["task-1", "task-2"], Notifications(poller, VmA).Select(x => x.taskId));
    }

    // And one message per machine, since a group is per machine. A client subscribed to one Vm must not
    // be told about another's work.
    [Fact]
    public async Task TasksForTwoVms_ArriveAsOneBroadcastEach()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, key: "task-a");
        vcenter.Doing(VmB, key: "task-b");
        await Seed(VsphereVm(VmA), VsphereVm(VmB));
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.Equal(2, poller.Hub.Of(Progress).Count);
        Assert.Equal("task-a", Assert.Single(Notifications(poller, VmA)).taskId);
        Assert.Equal("task-b", Assert.Single(Notifications(poller, VmB)).taskId);
    }

    /// <summary>
    /// A task whose entity is nothing this deployment knows about - a datastore operation, a host going
    /// into maintenance mode, another tenant's machine on a shared vCenter - is silently not broadcast.
    /// </summary>
    /// <remarks>
    /// It is not silently ignored, though: <c>TaskService.cs:201-209</c> sets <c>_tasksPending</c> from the
    /// state before it checks whether a Vm was resolved, so an unrelated vCenter task in a queued or
    /// running state holds this poller at its fast re-poll interval for as long as it lasts. That is
    /// covered by <see cref="ATaskWithNoVmBehindIt_StillHoldsThePollerOnTheFastInterval"/>.
    /// </remarks>
    [Fact]
    public async Task ATaskWhoseEntityIsNoKnownVm_ProducesNoNotification()
    {
        var vcenter = new Vcenter();
        vcenter.DoingSomethingUnrelated();
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.Empty(poller.Hub.Sends);
        Assert.Empty(poller.Errors);
    }

    // A task with no entity at all is not even looked up. vCenter reports these for work that is not
    // against a managed object, and a null moref reaching the connection cache is the kind of thing the
    // per-task catch would turn into a logged exception every five seconds.
    [Fact]
    public async Task ATaskWithNoEntity_IsNotLookedUpAtAll()
    {
        var vcenter = new Vcenter();
        vcenter.Reports(new ObjectContent
        {
            propSet = [new DynamicProperty { name = "info.state", val = TaskInfoState.running }]
        });
        var poller = Poll(vcenter);

        await poller.Run();

        Assert.Empty(poller.Hub.Sends);
        poller.Connections.DidNotReceive().GetVmIdByRef(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// A moref is resolved against the vCenter that reported it, so the same moref reported by another
    /// vCenter is not mistaken for this machine.
    /// </summary>
    /// <remarks>
    /// Morefs are per-vCenter identifiers and <c>vm-123</c> exists on every vCenter of any size, which is
    /// why <c>GetVmIdByRef</c> takes the host as its second argument. On a single-vCenter install nothing
    /// would notice the argument being wrong; on a multi-vCenter one it would broadcast one machine's
    /// progress to a different machine's subscribers and flag the wrong row busy.
    /// </remarks>
    [Fact]
    public async Task ATaskWhoseEntityBelongsToAnotherVcenter_ProducesNoNotification()
    {
        var known = new Vcenter("vcenter-a.example.test");
        known.Knows(VmA);
        var other = new Vcenter("vcenter-b.example.test");
        other.Reports(Recent(Moref(VmA)));
        await Seed(VsphereVm(VmA));
        var poller = Poll(known, other);

        await poller.Run();

        Assert.Empty(poller.Hub.Sends);
    }

    #endregion

    #region The state check a finished power task triggers

    /// <summary>
    /// A power task finishing asks <c>IMachineStateService</c> to look again immediately.
    /// </summary>
    /// <remarks>
    /// This is what makes the power indicator in the UI change the moment a power-on completes rather
    /// than whenever the state poller next happens to run - which at the shipped
    /// <c>CheckTaskProgressIntervalMilliseconds</c> is up to five seconds later, on the machine the user
    /// is watching, after they pressed the button themselves.
    /// </remarks>
    [Theory]
    [InlineData(PowerOn)]
    [InlineData(PowerOff)]
    public async Task ASuccessfulPowerTask_AsksForAStateCheck(string type)
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.success, type: type);
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        poller.MachineState.Received(1).CheckState();
    }

    // Only the two power types, because only they change something the state poller reports. A snapshot
    // or a reconfigure finishing would otherwise cost a full sweep of every machine on every vCenter.
    [Fact]
    public async Task ASuccessfulTaskOfAnyOtherType_AsksForNoStateCheck()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.success, type: "VirtualMachine.reconfigure");
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        poller.MachineState.DidNotReceive().CheckState();
    }

    // And only on success: a power task that is still running has not changed the power state yet, so
    // asking now would read the state the user is waiting to see change.
    [Fact]
    public async Task APowerTaskStillRunning_AsksForNoStateCheck()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.running, type: PowerOn);
        await Seed(VsphereVm(VmA));
        var poller = Poll(vcenter);

        await poller.Run();

        poller.MachineState.DidNotReceive().CheckState();
    }

    // A power task on a machine this deployment does not own is somebody else's business. On a shared
    // vCenter that is most of the power tasks in the list, and each one would otherwise cost a sweep.
    [Fact]
    public async Task ASuccessfulPowerTaskForNoKnownVm_AsksForNoStateCheck()
    {
        var vcenter = new Vcenter();
        vcenter.DoingSomethingUnrelated(state: TaskInfoState.success, type: PowerOn);
        var poller = Poll(vcenter);

        await poller.Run();

        poller.MachineState.DidNotReceive().CheckState();
    }

    #endregion

    #region What one failure costs

    /// <summary>
    /// A vCenter that cannot be queried is logged by address and skipped, and every other vCenter in the
    /// same pass is still processed.
    /// </summary>
    /// <remarks>
    /// The reason <c>Task.WhenAll</c> is wrapped in a bare <c>catch</c> with a comment instead of being
    /// awaited normally: <c>WhenAll</c> surfaces only the first fault, so one unreachable vCenter awaited
    /// naively would abandon the pass - no broadcast for any machine anywhere, and every flag left as it
    /// was. The outcome per connection is therefore read off each task individually. The address in the
    /// log is the only thing that says which vCenter is down.
    /// </remarks>
    [Fact]
    public async Task AConnectionThatCannotBeReached_IsLoggedAndTheOthersStillProcessed()
    {
        var unreachable = new Vcenter("vcenter-down.example.test");
        unreachable.CannotBeReached("The socket connection was aborted");
        var healthy = new Vcenter("vcenter-up.example.test");
        healthy.Doing(VmA, state: TaskInfoState.running);
        await Seed(VsphereVm(VmA));
        var poller = Poll(unreachable, healthy);

        await poller.Run();

        var logged = Assert.Single(poller.Errors);
        Assert.Contains("vcenter-down.example.test", logged.Message);

        Assert.Equal<string>([VmA.ToString()], poller.Hub.Recipients(Progress));
        Assert.True(await Pending(VmA));
    }

    /// <summary>
    /// An exception thrown on the way into the query - not a faulted task but a throw - is logged and the
    /// loop keeps turning.
    /// </summary>
    /// <remarks>
    /// The query is built and submitted in the loop over connections, which no inner <c>try</c> covers, so
    /// a client whose channel has faulted throws from there and costs the whole pass rather than one
    /// connection. What this pins is only that the service survives it: a poller that stopped turning
    /// would leave every spinner in the UI on until the next deploy, and nothing else in the application
    /// would report it.
    /// </remarks>
    [Fact]
    public async Task AnExceptionInThePass_IsLoggedAndTheLoopKeepsTurning()
    {
        var vcenter = new Vcenter();
        vcenter.Throws(new TimeoutException("the channel is faulted"));
        var poller = Poll(vcenter);

        await poller.Run(passes: 2);

        Assert.Equal(2, vcenter.Filters.Count);
        Assert.Equal(2, poller.Errors.Count());
        Assert.All(poller.Errors, x => Assert.IsType<TimeoutException>(x.Exception));
    }

    /// <summary>
    /// A broadcast that fails for one group is logged and the other groups still get theirs.
    /// </summary>
    /// <remarks>
    /// The <c>try</c> is inside the loop over groups rather than around it, which is what makes this true
    /// - and the flags have already been written by then, so a machine whose notification is lost still
    /// stops looking busy on the next pass. The alternative would be one bad group costing every other
    /// machine's progress in the same pass.
    /// </remarks>
    [Fact]
    public async Task ABroadcastThatThrows_IsLoggedAndTheOtherGroupsStillHearTheirs()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, key: "task-a");
        vcenter.Doing(VmB, key: "task-b");
        await Seed(VsphereVm(VmA), VsphereVm(VmB));
        var poller = Poll(vcenter);
        poller.Hub.FailsFor(VmA.ToString(), new TimeoutException("the hub is gone"));

        await poller.Run();

        var logged = Assert.Single(poller.Errors);
        Assert.IsType<TimeoutException>(logged.Exception);

        Assert.Equal<string>([VmB.ToString()], poller.Hub.Recipients(Progress));
        Assert.Equal("task-b", Assert.Single(Notifications(poller, VmB)).taskId);
    }

    /// <summary>
    /// One task that cannot be processed is logged with the vCenter that reported it, and the rest of that
    /// vCenter's list is still processed.
    /// </summary>
    /// <remarks>
    /// The per-task <c>try</c>, which is the innermost of the three and the last line of this service left
    /// uncovered before this test. A recent-task list is tens of entries on a busy vCenter and it is
    /// walked in one loop, so without this the first entry the service could not make sense of would cost
    /// every entry after it - including the reconciliation that clears the spinners, since a task missing
    /// from <c>stillPendingVmIds</c> is indistinguishable from a task that finished.
    /// </remarks>
    [Fact]
    public async Task ATaskThatCannotBeProcessed_IsLoggedAndTheRestOfTheListStillIs()
    {
        var vcenter = new Vcenter();
        vcenter.Reports(Recent(Moref(VmB), key: "task-b"));
        vcenter.CannotTranslate(VmB, new InvalidOperationException("the connection cache is being rebuilt"));
        vcenter.Doing(VmA, state: TaskInfoState.running, key: "task-a");
        await Seed(VsphereVm(VmA), VsphereVm(VmB, pending: true));
        var poller = Poll(vcenter);

        await poller.Run();

        var logged = Assert.Single(poller.Errors);
        Assert.Contains("vcenter.example.test", logged.Message);

        Assert.Equal<string>([VmA.ToString()], poller.Hub.Recipients(Progress));
        Assert.Equal("task-a", Assert.Single(Notifications(poller, VmA)).taskId);
        Assert.True(await Pending(VmA));

        // And the machine whose own task could not be read loses its flag, because nothing put it on the
        // still-pending list. The spinner goes out while vCenter is still working.
        Assert.False(await Pending(VmB));
    }

    #endregion

    #region The property filter the query is built from

    // The filter is the whole of what makes vCenter return anything: it is rooted at the task manager and
    // walks its recentTask collection, so a wrong root or a wrong path produces an empty answer rather
    // than an error - which reads exactly like an idle vCenter and would leave the UI silent.
    [Fact]
    public async Task TheFilter_IsRootedAtTheTaskManagerAndTraversesRecentTask()
    {
        var vcenter = new Vcenter();
        var poller = Poll(vcenter);

        await poller.Run();

        var filter = Assert.Single(Assert.Single(vcenter.Filters));
        var objectSpec = Assert.Single(filter.objectSet);
        Assert.Equal(vcenter.Connection.Sic.taskManager, objectSpec.obj);
        Assert.False(objectSpec.skip);
        Assert.True(objectSpec.skipSpecified);

        var traversal = Assert.IsType<TraversalSpec>(Assert.Single(objectSpec.selectSet));
        Assert.Equal("TaskManager", traversal.type);
        Assert.Equal("recentTask", traversal.path);

        // Addressed to the connection's own property collector, which is the only reference a query can
        // be sent to at all.
        await vcenter.Client.Received(1).RetrievePropertiesAsync(
            vcenter.Connection.Props, Arg.Any<PropertyFilterSpec[]>());
    }

    /// <summary>
    /// The eight <c>Task</c> properties asked for. A property left out of this list is absent from the
    /// answer, so the notification field that reads it becomes an empty string with nothing logged.
    /// </summary>
    /// <remarks>
    /// Three of the eight are never read - <c>info.name</c>, <c>info.cancelled</c> and <c>info.error</c>.
    /// The name in a notification is derived from <c>info.descriptionId</c> instead, and a cancelled or
    /// failed task is only ever "not queued and not running" to this poller, so a user is told a task
    /// ended and never that it failed. Asserted as a set rather than a sequence: vCenter does not care
    /// about the order, and the order is not what a test should pin.
    /// </remarks>
    [Fact]
    public async Task TheFilter_AsksVcenterForTheEightTaskProperties()
    {
        var vcenter = new Vcenter();
        var poller = Poll(vcenter);

        await poller.Run();

        var properties = Assert.Single(Assert.Single(Assert.Single(vcenter.Filters)).propSet);
        Assert.Equal("Task", properties.type);
        Assert.False(properties.all);

        string[] expected =
        [
            "info.cancelled",
            "info.descriptionId",
            "info.entity",
            "info.error",
            "info.key",
            "info.name",
            "info.progress",
            "info.state",
        ];

        Assert.Equal(expected, properties.pathSet.Order());
    }

    #endregion

    #region The readiness probe

    /// <summary>
    /// Every pass stamps the health check and re-reads its allowance from configuration.
    /// </summary>
    /// <remarks>
    /// This poller is the only thing that ever writes either value, and the readiness route reports
    /// Unhealthy from them alone - so a pass that stopped calling <c>CompletedRun</c> would take the
    /// service out of rotation with nothing else about it changed and nothing else able to say why. The
    /// allowance is re-read every pass rather than captured, which is what lets it be widened on a
    /// running deployment.
    /// </remarks>
    [Fact]
    public async Task EveryPass_StampsTheHealthCheckAndTakesItsAllowanceFromConfiguration()
    {
        var poller = Poll(Configured(allowance: 180));
        var stale = DateTime.Now.AddHours(-1);
        poller.Health.LastRun = stale;

        await poller.Run();

        Assert.Equal(180, poller.Health.HealthAllowance);
        Assert.True(poller.Health.LastRun > stale, "the pass must have stamped LastRun");

        var report = await poller.Health.CheckHealthAsync(new HealthCheckContext(), Ct);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    /// <summary>
    /// With no allowance configured the probe reports Unhealthy immediately after a pass that did
    /// everything it was supposed to.
    /// </summary>
    /// <remarks>
    /// <c>HealthAllowanceSeconds</c> is an <c>int</c> with no default, and the check compares against it
    /// with a strict <c>&lt;</c>, so zero means "unresponsive however recently it ran". The class's own
    /// field default is 90 and this overwrites it on every pass, which means a deployment that sets any
    /// other <c>Vsphere</c> option without this one - an environment-variable install cannot inherit a
    /// single key from <c>appsettings.json</c> once it overrides the section - fails readiness forever
    /// while the poller works perfectly. <c>appsettings.json</c> ships 180.
    /// </remarks>
    [Fact]
    public async Task WithNoAllowanceConfigured_ReadinessFailsThoughThePassSucceeded()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.running);
        await Seed(VsphereVm(VmA));
        var poller = Poll(Configured(allowance: 0), vcenter);
        var stale = DateTime.Now.AddHours(-1);
        poller.Health.LastRun = stale;

        await poller.Run();

        Assert.True(await Pending(VmA));
        Assert.True(poller.Health.LastRun > stale, "the pass must have stamped LastRun");

        var report = await poller.Health.CheckHealthAsync(new HealthCheckContext(), Ct);
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
    }

    #endregion

    #region Intervals, waking and shutdown

    /// <summary>
    /// With a task still running the next pass comes on the re-check interval, which is the fast one.
    /// </summary>
    /// <remarks>
    /// This is what makes the progress bar move: the shipped intervals are 1000ms while something is
    /// running and 5000ms while nothing is. Driven with no nudge at all, so a second pass arriving is
    /// the assertion and only the pending arm of the ternary can produce it.
    /// </remarks>
    [Fact]
    public async Task WithATaskStillRunning_TheNextPassComesOnTheFastInterval()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.running);
        await Seed(VsphereVm(VmA));
        var poller = Poll(Configured(check: NeverOnItsOwn, recheck: 25), vcenter);

        await poller.RunUnprompted();

        Assert.True(poller.Loop.Passes >= 2);
    }

    // And the other arm, with the two intervals swapped and nothing running: the pass comes on the slow
    // one. Together these two say which interval is chosen rather than only that some interval is.
    [Fact]
    public async Task WithNothingRunning_TheNextPassComesOnTheSlowInterval()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.success);
        await Seed(VsphereVm(VmA, pending: true));
        var poller = Poll(Configured(check: 25, recheck: NeverOnItsOwn), vcenter);

        await poller.RunUnprompted();

        Assert.True(poller.Loop.Passes >= 2);
    }

    /// <summary>
    /// A queued or running task with no Player Vm behind it holds the poller at its fast interval too.
    /// </summary>
    /// <remarks>
    /// <c>_tasksPending</c> is set from the state before the code asks whether a Vm was resolved
    /// (<c>TaskService.cs:201-209</c>), so on a vCenter this deployment shares with anything else - a
    /// long datastore operation, another tenant's clone - this poller re-polls every vCenter on the fast
    /// interval for the duration, with nothing of its own to report. Harmless at one poll a second, and
    /// worth knowing before the fast interval is ever shortened.
    /// </remarks>
    [Fact]
    public async Task ATaskWithNoVmBehindIt_StillHoldsThePollerOnTheFastInterval()
    {
        var vcenter = new Vcenter();
        vcenter.DoingSomethingUnrelated(state: TaskInfoState.running);
        var poller = Poll(Configured(check: NeverOnItsOwn, recheck: 25), vcenter);

        await poller.RunUnprompted();

        Assert.True(poller.Loop.Passes >= 2);
    }

    /// <summary>
    /// Once nothing is running any more the poller drops back to the slow interval rather than staying
    /// fast for the rest of the process's life.
    /// </summary>
    /// <remarks>
    /// <c>_tasksPending</c> is a field, and the reset at the top of each pass
    /// (<c>TaskService.cs:76</c>) is the only thing that ever clears it. Without it the first task anyone
    /// starts would leave every vCenter polled on the fast interval forever - at the shipped intervals
    /// five times the query load, permanently, on every deployment. Arranged the other way round from the
    /// two tests above: the fast interval is the short one, so the second pass arrives on its own, and the
    /// third can only arrive if the reset did not happen.
    /// </remarks>
    [Fact]
    public async Task OnceNothingIsRunning_TheNextPassComesOnTheSlowIntervalAgain()
    {
        var vcenter = new Vcenter();
        vcenter.Doing(VmA, state: TaskInfoState.running);
        vcenter.OnlyOnce();
        await Seed(VsphereVm(VmA));
        var poller = Poll(Configured(check: NeverOnItsOwn, recheck: 25), vcenter);
        poller.Loop.AllowedPasses = 3;

        await poller.Service.StartAsync(Ct);

        try
        {
            await PollLoop.Until(() => poller.Loop.Passes >= 2, "a second pass on the fast interval");

            // Half a second against the configured minute the second pass should now be waiting out, so a
            // third pass appearing means the interval was chosen, not that the machine was slow.
            await Task.Delay(500, Ct);
            Assert.Equal(2, poller.Loop.Passes);
        }
        finally
        {
            await poller.StopNow();
        }
    }

    /// <summary>
    /// <c>CheckTasks</c> brings the next pass forward instead of waiting out the interval.
    /// </summary>
    /// <remarks>
    /// The <c>CheckTasks</c> pipeline behavior calls this after every power command, and it is the whole
    /// reason a user sees a spinner within a second of pressing the button rather than up to five seconds
    /// later. Every other test in this class depends on it through <see cref="PollLoop.Run"/>; this is the
    /// one that says so, with both intervals at a minute so nothing else can produce the second pass.
    /// </remarks>
    [Fact]
    public async Task CheckTasks_BringsTheNextPassForward()
    {
        var poller = Poll();
        poller.Loop.AllowedPasses = 2;
        var stale = DateTime.Now.AddHours(-1);
        poller.Health.LastRun = stale;

        await poller.Service.StartAsync(Ct);

        try
        {
            // CompletedRun is the last thing a pass does before it waits, so a moved LastRun says the
            // loop is sitting in the wait rather than still working.
            await PollLoop.Until(() => poller.Health.LastRun > stale, "the first pass to reach its wait");
            Assert.Equal(1, poller.Loop.Passes);

            poller.Service.CheckTasks();

            await PollLoop.Until(() => poller.Loop.Passes >= 2, "a second pass after CheckTasks", seconds: 5);
        }
        finally
        {
            await poller.StopNow();
        }
    }

    /// <summary>
    /// Cancelling the service does not wake it: it sleeps on for the rest of its interval, and only a
    /// nudge gets it out early.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A defect, characterized rather than fixed. <c>TaskService.cs:95</c> calls
    /// <c>_resetEvent.WaitAsync</c> with no cancellation token, where <c>ProxmoxTaskService.cs:109</c> and
    /// <c>MachineStateService.cs:80-82</c> both pass one. So a shutdown waits out up to a full
    /// <c>CheckTaskProgressIntervalMilliseconds</c> - five seconds as <c>appsettings.json</c> ships it -
    /// on every deployment, every restart and every rolling update, after which the container is killed
    /// rather than stopped if the orchestrator's grace period is shorter. It is also why
    /// <c>PollLoop.Stop</c> has to nudge after cancelling, and so why every test in this class depends on
    /// the defect being there.
    /// </para>
    /// <para>
    /// The fix is to pass the token, as the other two pollers do. This test will then fail, and the
    /// assertion to replace it with is that the stop completes without a nudge. The observation window is
    /// half a second against a configured minute, so it says the loop is asleep and not that it is slow.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhenCancelled_TheLoopSleepsOnUntilSomethingNudgesIt()
    {
        var poller = Poll();
        var stale = DateTime.Now.AddHours(-1);
        poller.Health.LastRun = stale;

        await poller.Service.StartAsync(Ct);
        await PollLoop.Until(() => poller.Health.LastRun > stale, "the first pass to reach its wait");

        var stopping = poller.Stopping();

        await Assert.ThrowsAsync<TimeoutException>(
            () => stopping.WaitAsync(TimeSpan.FromMilliseconds(500), Ct));

        poller.Service.CheckTasks();
        await stopping;
    }

    #endregion

    #region Arrangement

    /// <summary>
    /// The pending flag as it is stored, read through a cold change tracker: the pass writes through a
    /// context of its own, so a value read through <c>Db</c> could be one the pass never saved.
    /// </summary>
    private async Task<bool> Pending(Guid id)
    {
        await using var context = NewContext();

        return (await context.Vms.SingleAsync(x => x.Id == id, Ct)).HasPendingTasks;
    }

    /// <summary>The notifications one group was sent, flattened in the order they were broadcast.</summary>
    private static List<Notification> Notifications(Poller poller, Guid vmId) =>
        [.. poller.Hub.Of(Progress)
            .Where(x => x.Groups.Contains(vmId.ToString()))
            .SelectMany(x => (List<Notification>)x.Args[0])];

    private static VmEntity VsphereVm(Guid id, bool pending = false) =>
        new() { Id = id, Name = $"vm-{id.ToString()[..4]}", Type = VmType.Vsphere, HasPendingTasks = pending };

    /// <summary>
    /// Both intervals long enough that only a nudge turns the loop, and the allowance
    /// <c>appsettings.json</c> ships. The interval tests are the only ones that pass anything else.
    /// </summary>
    private static VsphereOptions Configured(
        int check = NeverOnItsOwn, int recheck = NeverOnItsOwn, int allowance = 180) =>
        new()
        {
            CheckTaskProgressIntervalMilliseconds = check,
            ReCheckTaskProgressIntervalMilliseconds = recheck,
            HealthAllowanceSeconds = allowance,
        };

    private Poller Poll(params Vcenter[] vcenters) => new(NewContext, Configured(), vcenters);

    private Poller Poll(VsphereOptions options, params Vcenter[] vcenters) => new(NewContext, options, vcenters);

    private static ManagedObjectReference Mor(string value, string type = "VirtualMachine") =>
        new() { type = type, Value = value };

    /// <summary>
    /// The moref a vCenter would hold for a Player Vm. Derived rather than stated so that a test names
    /// only the Vm id, which is the identifier both ends of this service are addressed by.
    /// </summary>
    private static string Moref(Guid vmId) => $"vm-{vmId.ToString()[..8]}";

    /// <summary>One entry of vCenter's recent-task list, as the property collector returns one.</summary>
    private static ObjectContent Recent(
        string moref,
        string key = "task-1",
        string type = PowerOn,
        int progress = 50,
        TaskInfoState state = TaskInfoState.running,
        string entityType = "VirtualMachine") =>
        new()
        {
            propSet =
            [
                new DynamicProperty { name = "info.entity", val = Mor(moref, entityType) },
                new DynamicProperty { name = "info.key", val = key },
                new DynamicProperty { name = "info.descriptionId", val = type },
                new DynamicProperty { name = "info.progress", val = progress },
                new DynamicProperty { name = "info.state", val = state },
            ]
        };

    /// <summary>
    /// One vCenter, holding whatever recent tasks a test gives it and whatever moref translations a live
    /// connection would have cached.
    /// </summary>
    /// <remarks>
    /// Built the way <c>VsphereServiceCommandTests.FakeVcenter</c> builds one, with the same substituted
    /// <see cref="IVimClient"/> as the seam. The answer is computed per call from mutable state rather
    /// than stubbed once, so the order a test arranges things in cannot matter.
    /// </remarks>
    private sealed class Vcenter
    {
        private readonly List<ObjectContent> _tasks = [];
        private readonly Dictionary<string, Guid> _known = [];
        private readonly Dictionary<string, Exception> _unreadable = [];
        private Func<Task<RetrievePropertiesResponse>> _answer;

        public Vcenter(string address = "vcenter.example.test")
        {
            Connection = new VsphereConnection(
                new VsphereHost { Enabled = true, Address = address },
                new VsphereOptions(),
                NullLogger.Instance)
            {
                Client = Client,
                Props = new ManagedObjectReference { type = "PropertyCollector", Value = "propertyCollector" },
                Sic = new ServiceContent
                {
                    taskManager = new ManagedObjectReference { type = "TaskManager", Value = "TaskManager" }
                }
            };

            _answer = () => Task.FromResult(new RetrievePropertiesResponse([.. _tasks]));

            Client.RetrievePropertiesAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<PropertyFilterSpec[]>())
                .Returns(call =>
                {
                    Filters.Add(call.ArgAt<PropertyFilterSpec[]>(1));
                    return _answer();
                });
        }

        public IVimClient Client { get; } = Substitute.For<IVimClient>();

        public VsphereConnection Connection { get; }

        /// <summary>
        /// Every filter this vCenter was queried with, which is also the count of queries: a connection
        /// the poller skips has none.
        /// </summary>
        public List<PropertyFilterSpec[]> Filters { get; } = [];

        /// <summary>A task against a Player Vm, whose moref this vCenter can therefore also translate.</summary>
        public void Doing(
            Guid vmId,
            TaskInfoState state = TaskInfoState.running,
            string type = PowerOn,
            string key = "task-1",
            int progress = 50)
        {
            Knows(vmId);
            Reports(Recent(Moref(vmId), key, type, progress, state));
        }

        /// <summary>
        /// A task on an entity no Player Vm corresponds to: a datastore operation, a host action, or a
        /// machine belonging to something else sharing this vCenter.
        /// </summary>
        public void DoingSomethingUnrelated(
            TaskInfoState state = TaskInfoState.running, string type = "Datastore.deleteFile") =>
            Reports(Recent("datastore-17", type: type, state: state, entityType: "Datastore"));

        /// <summary>The moref translation a live connection's cache would hold, with no task attached.</summary>
        public void Knows(Guid vmId) => _known[Moref(vmId)] = vmId;

        public void Reports(params ObjectContent[] tasks) => _tasks.AddRange(tasks);

        /// <summary>
        /// Answers the first query with what this vCenter was given and every later one with an empty
        /// list, which is a task that has aged out of the recent-task list between two passes.
        /// </summary>
        /// <remarks>
        /// Arranged here rather than by clearing the tasks from the test between passes, because that is a
        /// race the test loses about as often as it wins: the loop is on another thread and the only signal
        /// a test has is that a pass <em>started</em>, by which point it may already have queried.
        /// </remarks>
        public void OnlyOnce()
        {
            var first = _tasks.ToArray();
            var answered = false;

            _answer = () =>
            {
                ObjectContent[] returned = answered ? [] : first;
                answered = true;

                return Task.FromResult(new RetrievePropertiesResponse(returned));
            };
        }

        /// <summary>
        /// A moref this vCenter reports and whose translation then throws, which is the only way into the
        /// per-task <c>catch</c>: everything else in that block reads properties off the answer.
        /// </summary>
        public void CannotTranslate(Guid vmId, Exception failure) => _unreadable[Moref(vmId)] = failure;

        /// <summary>A vCenter that answers with a SOAP fault, as an unreachable host or a stale session does.</summary>
        public void CannotBeReached(string reason) =>
            _answer = () => Task.FromException<RetrievePropertiesResponse>(new FaultException(reason));

        /// <summary>A client that throws where it is called rather than returning a faulted task.</summary>
        public void Throws(Exception exception) => _answer = () => throw exception;

        public Guid? Resolve(string moref) =>
            _unreadable.TryGetValue(moref, out var failure)
                ? throw failure
                : _known.TryGetValue(moref, out var id) ? id : null;
    }

    /// <summary>
    /// A <c>TaskService</c> over a set of vCenters, with everything it reports to recorded.
    /// </summary>
    private sealed class Poller
    {
        private readonly Vcenter[] _vcenters;

        public Poller(Func<VmContext> newContext, VsphereOptions options, Vcenter[] vcenters)
        {
            _vcenters = vcenters;
            Loop = new PollLoop(newContext);

            Connections.GetAllConnections().Returns([.. vcenters.Select(x => x.Connection)]);

            // Resolved live and per vCenter, which is what ConnectionService does: a moref means nothing
            // without the host that reported it.
            Connections.GetVmIdByRef(Arg.Any<string>(), Arg.Any<string>())
                .Returns(call => Resolve(call.ArgAt<string>(0), call.ArgAt<string>(1)));

            var monitor = Substitute.For<IOptionsMonitor<VsphereOptions>>();
            monitor.CurrentValue.Returns(options);

            Service = new TaskService(
                monitor, Log, Hub.Context, Connections, MachineState, Loop, Health);
        }

        public IConnectionService Connections { get; } = Substitute.For<IConnectionService>();

        public IMachineStateService MachineState { get; } = Substitute.For<IMachineStateService>();

        public HubContextHarness<ProgressHub> Hub { get; } = new();

        public RecordingLogger<TaskService> Log { get; } = new();

        public TaskServiceHealthCheck Health { get; } = new();

        public PollLoop Loop { get; }

        public TaskService Service { get; }

        /// <summary>
        /// The errors the pass itself logged. <see cref="PollLoop"/> refuses the pass after the last
        /// allowed one and the service logs that refusal like any other exception, so the raw count of
        /// Error entries is not a count of things that went wrong.
        /// </summary>
        public IEnumerable<RecordingLogger.LogEntry> Errors =>
            Log.At(LogLevel.Error)
                .Where(x => x.Exception is not InvalidOperationException
                    || !x.Exception.Message.StartsWith("PollLoop", StringComparison.Ordinal));

        public Task Run(int passes = 1) => Loop.Run(Service, Service.CheckTasks, passes);

        public Task RunUnprompted(int passes = 2) => Loop.RunUnprompted(Service, Service.CheckTasks, passes);

        /// <summary>
        /// Cancels and waits, nudging afterwards - what <see cref="PollLoop.Stop"/> does, and for the same
        /// reason: without the nudge this service sits out the rest of its interval first.
        /// </summary>
        public async Task StopNow()
        {
            var stopping = Stopping();

            Service.CheckTasks();

            await stopping;
        }

        /// <summary>
        /// Cancellation on its own, for the test whose subject is what the loop does with it. Kept out of
        /// the test method because <c>CancellationToken.None</c> is the point of it.
        /// </summary>
        public Task Stopping() => Service.StopAsync(CancellationToken.None);

        private Guid? Resolve(string moref, string host) =>
            _vcenters.FirstOrDefault(x => x.Connection.Address == host)?.Resolve(moref);
    }

    #endregion
}
