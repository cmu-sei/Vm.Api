// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net.Http;
using System.ServiceModel;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using VimClient;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// Characterization tests for the power commands VsphereService sends to vCenter, driven through a
/// substituted <see cref="IVimClient"/> so no vCenter is involved.
///
/// These pin down a contract that looks like sloppy error handling and is not: the VM UI lets a user
/// multi-select machines and hit power on, so a single VM that is already on - or a single VM whose
/// host is unreachable - must not surface as an error for the whole selection. The service therefore
/// reports outcomes as opaque strings and swallows faults on some paths on purpose. Several
/// assertions below would look wrong to someone reading them as "what good code does"; they are here
/// so that intent survives the next refactor.
/// </summary>
public class VsphereServiceCommandTests
{
    private static readonly Guid VmA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid VmB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ManagedObjectReference Mor(string type, string value) =>
        new() { type = type, Value = value };

    // What the property collector hands back for "summary.runtime.powerState". GetPowerState reads the
    // val's runtime type, so the enum has to be boxed in as-is.
    private static RetrievePropertiesResponse PowerStateResponse(VirtualMachinePowerState state) =>
        new([
            new ObjectContent
            {
                propSet = [new DynamicProperty { name = "summary.runtime.powerState", val = state }]
            }
        ]);

    private static RetrievePropertiesResponse TaskInfoResponse(TaskInfoState state) =>
        new([
            new ObjectContent
            {
                propSet = [new DynamicProperty { name = "info", val = new TaskInfo { state = state } }]
            }
        ]);

    // Both the power-state precheck and the task-progress poll go through RetrievePropertiesAsync, so
    // the fake routes on what the filter asks for - VsphereService.TaskFilter is the only one that
    // asks for the "Task" type. Without this, stubbing one clobbers the other.
    private static bool IsTaskFilter(PropertyFilterSpec[] specs) =>
        specs[0].propSet[0].type == "Task";

    // How a SOAP fault arrives: the generated client returns a faulted task rather than throwing
    // synchronously, which is what the bulk paths inspect to attribute a failure to one VM.
    private static Task<T> Faulted<T>(string reason) =>
        Task.FromException<T>(new FaultException(reason));

    private static Task FaultedVoid(string reason) =>
        Task.FromException(new FaultException(reason));

    /// <summary>
    /// One vCenter holding whatever machines a test registers, standing in for what ConnectionService
    /// would have cached from a live connection.
    /// </summary>
    private sealed class FakeVcenter
    {
        public readonly IVimClient Client = Substitute.For<IVimClient>();
        public readonly IConnectionService Connections = Substitute.For<IConnectionService>();

        // Zero poll interval so the paths that wait on a vCenter task run without a real delay. At the
        // production default of 1000ms each of those tests would cost a second.
        public readonly VsphereOptions Options = new() { TaskPollIntervalMilliseconds = 0 };

        private readonly VsphereConnection _connection;

        public FakeVcenter()
        {
            _connection = new VsphereConnection(
                new VsphereHost { Enabled = true, Address = "vcenter.example.test" },
                Options,
                NullLogger.Instance)
            {
                Client = Client,
                Props = Mor("PropertyCollector", "propertyCollector"),
                Sic = new ServiceContent
                {
                    propertyCollector = Mor("PropertyCollector", "propertyCollector"),
                    searchIndex = Mor("SearchIndex", "SearchIndex")
                }
            };

            // GetVm falls back to searching every connection when the cache misses. Registering the
            // connection means an unregistered VM takes that path and comes back empty (FindByUuidAsync
            // returns null by default), which is what a deleted-in-vCenter machine looks like.
            Connections.GetAllConnections().Returns([_connection]);
        }

        public VsphereService Service() =>
            new(Microsoft.Extensions.Options.Options.Create(new RewriteHostOptions()),
                NullLogger<VsphereService>.Instance,
                Options,
                Substitute.For<IConfiguration>(),
                Connections,
                Substitute.For<IMapper>(),
                Substitute.For<IHttpClientFactory>());

        /// <summary>
        /// A machine the connection cache can resolve, with no power state wired up. For the bulk
        /// paths, which submit without asking vCenter what state the VM is in.
        /// </summary>
        public ManagedObjectReference AddVm(Guid id)
        {
            var reference = Mor("VirtualMachine", $"vm-{id.ToString()[..8]}");
            Connections.GetAggregate(id).Returns(new VsphereAggregate(_connection, reference));
            return reference;
        }

        /// <summary>
        /// A machine that also reports a power state. Pass more than one state for a command that reads
        /// the state twice - reboot reads it before powering off and again before powering on - and the
        /// last one repeats thereafter.
        ///
        /// The stub matches any VM filter rather than this VM's, so only wire states for one VM per
        /// test. That is all the single-VM commands need, since each looks up exactly one machine.
        /// </summary>
        public ManagedObjectReference AddVm(Guid id, params VirtualMachinePowerState[] states)
        {
            var reference = AddVm(id);
            var responses = Array.ConvertAll(states, PowerStateResponse);

            Client.RetrievePropertiesAsync(
                    Arg.Any<ManagedObjectReference>(),
                    Arg.Is<PropertyFilterSpec[]>(x => !IsTaskFilter(x)))
                .Returns(responses[0], responses[1..]);

            return reference;
        }

        /// <summary>What the property collector reports for a vCenter task this service is waiting on.</summary>
        public void TaskFinishes(TaskInfoState state) =>
            Client.RetrievePropertiesAsync(
                    Arg.Any<ManagedObjectReference>(),
                    Arg.Is<PropertyFilterSpec[]>(x => IsTaskFilter(x)))
                .Returns(TaskInfoResponse(state));

        /// <summary>An unreachable host, or a session that has gone stale, at power-state query time.</summary>
        public void PowerStateQueryFaults() =>
            Client.RetrievePropertiesAsync(
                    Arg.Any<ManagedObjectReference>(),
                    Arg.Is<PropertyFilterSpec[]>(x => !IsTaskFilter(x)))
                .Returns(Faulted<RetrievePropertiesResponse>("host unreachable"));

        /// <summary>A VM destroyed between the connection cache read and the power-state query.</summary>
        public void PowerStateQueryReturnsNothing() =>
            Client.RetrievePropertiesAsync(
                    Arg.Any<ManagedObjectReference>(),
                    Arg.Is<PropertyFilterSpec[]>(x => !IsTaskFilter(x)))
                .Returns(new RetrievePropertiesResponse([]));
    }

    #region PowerOnVm

    // The multi-select case: powering on a selection that includes already-running machines is normal
    // use, not an error, so the service answers from the power state without troubling vCenter.
    [Fact]
    public async Task PowerOn_WhenAlreadyOn_ReportsItAndSendsNothing()
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOn);

        var state = await vcenter.Service().PowerOnVm(VmA);

        Assert.Equal("already running", state);
        await vcenter.Client.DidNotReceive()
            .PowerOnVM_TaskAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<ManagedObjectReference>());
    }

    [Fact]
    public async Task PowerOn_WhenOff_SubmitsTheTaskForThatMachine()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOff);

        var state = await vcenter.Service().PowerOnVm(VmA);

        // "submitted", not "on": the service deliberately does not wait for the vCenter task to finish.
        Assert.Equal("poweron submitted", state);
        await vcenter.Client.Received(1).PowerOnVM_TaskAsync(reference, null);
    }

    // INTENTIONAL, DO NOT "FIX": a failed power-on reports the same string as a successful one. The UI
    // powers on a whole multi-select in one gesture, and a VM that raced into the on state between the
    // state check and the call (or sits on a host that is briefly unreachable) must not turn the user's
    // batch red. The catch block assigns "poweron error" and the line after it overwrites that
    // unconditionally - the dead assignment is the swallow.
    [Fact]
    public async Task PowerOn_WhenVcenterRejectsTheTask_StillReportsSubmitted()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOff);
        vcenter.Client.PowerOnVM_TaskAsync(reference, null)
            .Returns(Faulted<ManagedObjectReference>("InvalidPowerState"));

        var state = await vcenter.Service().PowerOnVm(VmA);

        Assert.Equal("poweron submitted", state);
    }

    // A VM the cache cannot resolve and no connection can find: null, and nothing is sent. Callers
    // treat this as "gone" rather than as a failure to report.
    [Fact]
    public async Task PowerOn_WhenTheMachineIsNotOnAnyConnection_ReportsNothing()
    {
        var vcenter = new FakeVcenter();

        var state = await vcenter.Service().PowerOnVm(VmA);

        Assert.Null(state);
        await vcenter.Client.DidNotReceive()
            .PowerOnVM_TaskAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<ManagedObjectReference>());
    }

    // The precheck sits outside the try that swallows power-on failures, so it used to throw straight
    // out of PowerOnVm - the one outcome the swallow exists to prevent, and reachable from a single
    // unreachable host in a multi-select. GetPowerState now answers "error" instead of throwing, which
    // is not "on", so the power-on is attempted anyway.
    [Fact]
    public async Task PowerOn_WhenTheStateCheckFaults_TriesAnyway()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA);
        vcenter.PowerStateQueryFaults();

        var state = await vcenter.Service().PowerOnVm(VmA);

        Assert.Equal("poweron submitted", state);
        await vcenter.Client.Received(1).PowerOnVM_TaskAsync(reference, null);
    }

    // Same gap, second flavor: a machine destroyed in vCenter between the cache read and the state
    // query answers with no objects, which used to index past the end of an empty array.
    [Fact]
    public async Task PowerOn_WhenTheStateCheckComesBackEmpty_TriesAnyway()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA);
        vcenter.PowerStateQueryReturnsNothing();

        var state = await vcenter.Service().PowerOnVm(VmA);

        Assert.Equal("poweron submitted", state);
        await vcenter.Client.Received(1).PowerOnVM_TaskAsync(reference, null);
    }

    #endregion

    #region PowerOffVm

    [Fact]
    public async Task PowerOff_WhenAlreadyOff_ReportsItAndSendsNothing()
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOff);

        var state = await vcenter.Service().PowerOffVm(VmA);

        Assert.Equal("already off", state);
        await vcenter.Client.DidNotReceive().PowerOffVM_TaskAsync(Arg.Any<ManagedObjectReference>());
    }

    [Fact]
    public async Task PowerOff_WhenOn_SubmitsTheTaskForThatMachine()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOn);

        var state = await vcenter.Service().PowerOffVm(VmA);

        Assert.Equal("poweroff submitted", state);
        await vcenter.Client.Received(1).PowerOffVM_TaskAsync(reference);
    }

    // Power off does NOT swallow the way power on does - same shape of code, but nothing overwrites the
    // error string, so the caller can tell the difference here and not there. Pinned because the
    // asymmetry is invisible when reading either method on its own.
    [Fact]
    public async Task PowerOff_WhenVcenterRejectsTheTask_ReportsTheError()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOn);
        vcenter.Client.PowerOffVM_TaskAsync(reference)
            .Returns(Faulted<ManagedObjectReference>("InvalidPowerState"));

        var state = await vcenter.Service().PowerOffVm(VmA);

        Assert.Equal("poweroff error", state);
    }

    [Fact]
    public async Task PowerOff_WhenTheMachineIsNotOnAnyConnection_ReportsNothing()
    {
        var vcenter = new FakeVcenter();

        Assert.Null(await vcenter.Service().PowerOffVm(VmA));
    }

    #endregion

    #region ShutdownVm

    [Fact]
    public async Task Shutdown_WhenAlreadyOff_ReportsItAndSendsNothing()
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOff);

        var state = await vcenter.Service().ShutdownVm(VmA);

        Assert.Equal("already off", state);
        await vcenter.Client.DidNotReceive().ShutdownGuestAsync(Arg.Any<ManagedObjectReference>());
    }

    // Shutdown is a guest-tools request, not a power operation: it goes to the running OS.
    [Fact]
    public async Task Shutdown_WhenOn_AsksTheGuestToShutDown()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOn);

        var state = await vcenter.Service().ShutdownVm(VmA);

        Assert.Equal("shutdown submitted", state);
        await vcenter.Client.Received(1).ShutdownGuestAsync(reference);
    }

    // A third error vocabulary for the same class of failure: "error" here, "poweroff error" for power
    // off, a success string for power on.
    [Fact]
    public async Task Shutdown_WhenTheGuestRequestFails_ReportsError()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOn);
        vcenter.Client.ShutdownGuestAsync(reference).Returns(FaultedVoid("no guest tools"));

        Assert.Equal("error", await vcenter.Service().ShutdownVm(VmA));
    }

    [Fact]
    public async Task Shutdown_WhenTheMachineIsNotOnAnyConnection_ReportsError()
    {
        var vcenter = new FakeVcenter();

        Assert.Equal("error", await vcenter.Service().ShutdownVm(VmA));
    }

    #endregion

    #region RebootVm

    [Fact]
    public async Task Reboot_WhenTheMachineIsNotOnAnyConnection_ReportsError()
    {
        var vcenter = new FakeVcenter();

        Assert.Equal("error", await vcenter.Service().RebootVm(VmA));
        await vcenter.Client.DidNotReceive().PowerOffVM_TaskAsync(Arg.Any<ManagedObjectReference>());
    }

    // Reboot is the one command that powers a machine off, so unlike power on it refuses to start when
    // the power state is unreadable rather than risk cycling a machine whose state it does not know. A
    // suspended VM reads as "error" here, which is how that unreadable state is spelled.
    [Fact]
    public async Task Reboot_WhenThePowerStateIsUnreadable_ReportsErrorAndSendsNothing()
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmA, VirtualMachinePowerState.suspended);

        Assert.Equal("error", await vcenter.Service().RebootVm(VmA));
        await vcenter.Client.DidNotReceive().PowerOffVM_TaskAsync(Arg.Any<ManagedObjectReference>());
    }

    // The same refusal now covers an unreachable host, which used to throw out of the method. Reboot is
    // where GetPowerState answering "error" instead of throwing has to NOT become "try anyway".
    [Fact]
    public async Task Reboot_WhenTheStateCheckFaults_ReportsErrorAndSendsNothing()
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmA);
        vcenter.PowerStateQueryFaults();

        Assert.Equal("error", await vcenter.Service().RebootVm(VmA));
        await vcenter.Client.DidNotReceive().PowerOffVM_TaskAsync(Arg.Any<ManagedObjectReference>());
    }

    // Unlike every other command here, reboot waits: it polls the power-off task to completion before
    // powering back on, so that the guest is not asked to start while it is still stopping. Two power
    // states because it reads the state again on the way into PowerOnVm.
    [Fact]
    public async Task Reboot_PowersOffAndWaitsForThatTaskBeforePoweringOn()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOn, VirtualMachinePowerState.poweredOff);
        vcenter.TaskFinishes(TaskInfoState.success);

        var state = await vcenter.Service().RebootVm(VmA);

        Assert.Equal("poweron submitted", state);
        await vcenter.Client.Received(1).PowerOffVM_TaskAsync(reference);
        await vcenter.Client.Received(1).PowerOnVM_TaskAsync(reference, null);
    }

    // If the power-off never succeeds, the machine is left alone rather than powered on into an
    // unknown state.
    [Fact]
    public async Task Reboot_WhenThePowerOffTaskFails_ReportsErrorAndDoesNotPowerOn()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOn);
        vcenter.TaskFinishes(TaskInfoState.error);

        Assert.Equal("error", await vcenter.Service().RebootVm(VmA));
        await vcenter.Client.Received(1).PowerOffVM_TaskAsync(reference);
        await vcenter.Client.DidNotReceive()
            .PowerOnVM_TaskAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<ManagedObjectReference>());
    }

    #endregion

    #region BulkShutdown and BulkReboot

    // The contract the whole design rests on: one VM's failure is reported against that VM and the rest
    // of the selection still gets its command.
    [Fact]
    public async Task BulkShutdown_WhenOneMachineFails_ReportsOnlyThatOneAndStillSendsTheRest()
    {
        var vcenter = new FakeVcenter();
        var failing = vcenter.AddVm(VmA);
        var healthy = vcenter.AddVm(VmB);
        vcenter.Client.ShutdownGuestAsync(failing).Returns(FaultedVoid("no guest tools"));

        var results = await vcenter.Service().BulkShutdown([VmA, VmB]);

        Assert.Equal("no guest tools", results[VmA]);
        Assert.Equal(string.Empty, results[VmB]);
        await vcenter.Client.Received(1).ShutdownGuestAsync(healthy);
    }

    // Reported per VM, so the batch is not held up by a machine that no longer exists.
    [Fact]
    public async Task BulkShutdown_WhenAMachineIsNotOnAnyConnection_SaysSoForThatMachineOnly()
    {
        var vcenter = new FakeVcenter();
        var healthy = vcenter.AddVm(VmB);

        var results = await vcenter.Service().BulkShutdown([VmA, VmB]);

        Assert.Equal("Virtual machine not found", results[VmA]);
        Assert.Equal(string.Empty, results[VmB]);
        await vcenter.Client.Received(1).ShutdownGuestAsync(healthy);
    }

    [Fact]
    public async Task BulkReboot_WhenOneMachineFails_ReportsOnlyThatOneAndStillSendsTheRest()
    {
        var vcenter = new FakeVcenter();
        var failing = vcenter.AddVm(VmA);
        var healthy = vcenter.AddVm(VmB);
        vcenter.Client.RebootGuestAsync(failing).Returns(FaultedVoid("no guest tools"));

        var results = await vcenter.Service().BulkReboot([VmA, VmB]);

        Assert.Equal("no guest tools", results[VmA]);
        Assert.Equal(string.Empty, results[VmB]);
        await vcenter.Client.Received(1).RebootGuestAsync(healthy);
    }

    [Fact]
    public async Task BulkReboot_WhenAMachineIsNotOnAnyConnection_SaysSoForThatMachineOnly()
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmB);

        var results = await vcenter.Service().BulkReboot([VmA, VmB]);

        Assert.Equal("Virtual machine not found", results[VmA]);
        Assert.Equal(string.Empty, results[VmB]);
    }

    #endregion

    #region BulkPowerOperation

    [Fact]
    public async Task BulkPowerOperation_SubmitsOneTaskPerMachine()
    {
        var vcenter = new FakeVcenter();
        var first = vcenter.AddVm(VmA);
        var second = vcenter.AddVm(VmB);

        var results = await vcenter.Service().BulkPowerOperation([VmA, VmB], PowerOperation.PowerOn);

        Assert.Equal(string.Empty, results[VmA]);
        Assert.Equal(string.Empty, results[VmB]);
        await vcenter.Client.Received(1).PowerOnVM_TaskAsync(first, null);
        await vcenter.Client.Received(1).PowerOnVM_TaskAsync(second, null);
    }

    // Unlike the single-VM path, the bulk path does not check power state first, so a selection that
    // includes already-running machines sends PowerOnVM_Task to all of them and lets vCenter object -
    // which is why the per-VM error reporting below matters more here than anywhere else.
    [Fact]
    public async Task BulkPowerOperation_DoesNotCheckPowerStateFirst()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA, VirtualMachinePowerState.poweredOn);

        await vcenter.Service().BulkPowerOperation([VmA], PowerOperation.PowerOn);

        await vcenter.Client.Received(1).PowerOnVM_TaskAsync(reference, null);
    }

    // Same string and same shape as BulkShutdown and the Proxmox equivalent. This used to be dropped
    // from the result silently, leaving the caller to infer it from an array shorter than the id list.
    [Fact]
    public async Task BulkPowerOperation_WhenAMachineIsNotOnAnyConnection_SaysSoForThatMachineOnly()
    {
        var vcenter = new FakeVcenter();
        var healthy = vcenter.AddVm(VmB);

        var results = await vcenter.Service().BulkPowerOperation([VmA, VmB], PowerOperation.PowerOn);

        Assert.Equal("Virtual machine not found", results[VmA]);
        Assert.Equal(string.Empty, results[VmB]);
        await vcenter.Client.Received(1).PowerOnVM_TaskAsync(healthy, null);
    }

    // The reason this method was rewritten. The old version awaited Task.WhenAll and read .Result
    // outside its try, so one rejected VM - an already-on machine in a multi-select power on, exactly
    // the case the single-VM path guards against - threw out of the whole call. The healthy VM's
    // command had already reached vCenter, so what was lost was only the reporting.
    [Fact]
    public async Task BulkPowerOperation_WhenOneMachineIsRejected_ReportsThatOneAndStillReportsTheRest()
    {
        var vcenter = new FakeVcenter();
        var failing = vcenter.AddVm(VmA);
        var healthy = vcenter.AddVm(VmB);
        vcenter.Client.PowerOnVM_TaskAsync(failing, null)
            .Returns(Faulted<ManagedObjectReference>("InvalidPowerState"));

        var results = await vcenter.Service().BulkPowerOperation([VmA, VmB], PowerOperation.PowerOn);

        Assert.Equal("InvalidPowerState", results[VmA]);
        Assert.Equal(string.Empty, results[VmB]);
        await vcenter.Client.Received(1).PowerOnVM_TaskAsync(healthy, null);
    }

    // Every VM rejected still returns rather than throwing: WhenAll surfaces only the first fault, so
    // the outcome has to be read off each task individually.
    [Fact]
    public async Task BulkPowerOperation_WhenEveryMachineIsRejected_ReportsEachOne()
    {
        var vcenter = new FakeVcenter();
        var first = vcenter.AddVm(VmA);
        var second = vcenter.AddVm(VmB);
        vcenter.Client.PowerOnVM_TaskAsync(first, null)
            .Returns(Faulted<ManagedObjectReference>("first failed"));
        vcenter.Client.PowerOnVM_TaskAsync(second, null)
            .Returns(Faulted<ManagedObjectReference>("second failed"));

        var results = await vcenter.Service().BulkPowerOperation([VmA, VmB], PowerOperation.PowerOn);

        Assert.Equal("first failed", results[VmA]);
        Assert.Equal("second failed", results[VmB]);
    }

    // Shutdown and Reboot arrive here from the API but have no case in the switch - they are routed to
    // BulkShutdown/BulkReboot by the handler instead. Saying so beats returning success for a command
    // that was never sent.
    [Fact]
    public async Task BulkPowerOperation_ForAnOperationItDoesNotHandle_SaysSoRatherThanReportingSuccess()
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmA);

        var results = await vcenter.Service().BulkPowerOperation([VmA], PowerOperation.Shutdown);

        Assert.Equal("Unsupported Operation", results[VmA]);
    }

    [Fact]
    public async Task BulkPowerOperation_PowerOff_SendsPowerOffTasks()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA);

        await vcenter.Service().BulkPowerOperation([VmA], PowerOperation.PowerOff);

        await vcenter.Client.Received(1).PowerOffVM_TaskAsync(reference);
        await vcenter.Client.DidNotReceive()
            .PowerOnVM_TaskAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<ManagedObjectReference>());
    }

    // Revert is routed through the same bulk entry point as power on/off, reverting to whatever the
    // current snapshot is, and suppressPowerOn is false so a reverted VM comes back running.
    [Fact]
    public async Task BulkPowerOperation_Revert_RevertsToTheCurrentSnapshotWithoutSuppressingPowerOn()
    {
        var vcenter = new FakeVcenter();
        var reference = vcenter.AddVm(VmA);

        await vcenter.Service().BulkPowerOperation([VmA], PowerOperation.Revert);

        await vcenter.Client.Received(1).RevertToCurrentSnapshot_TaskAsync(reference, null, false);
    }

    #endregion

    #region GetPowerState

    // Every power command uses this as a precheck and reads "error" as "state unknown", so it has to
    // answer rather than throw however badly the query goes. These two are the direct statement of
    // that guarantee; the PowerOn and Reboot tests above cover what each caller then does with it.
    [Fact]
    public async Task GetPowerState_WhenTheQueryFaults_ReportsErrorRatherThanThrowing()
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmA);
        vcenter.PowerStateQueryFaults();

        Assert.Equal("error", await vcenter.Service().GetPowerState(VmA));
    }

    [Fact]
    public async Task GetPowerState_WhenTheQueryReturnsNoObjects_ReportsErrorRatherThanThrowing()
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmA);
        vcenter.PowerStateQueryReturnsNothing();

        Assert.Equal("error", await vcenter.Service().GetPowerState(VmA));
    }

    [Theory]
    [InlineData(VirtualMachinePowerState.poweredOn, "on")]
    [InlineData(VirtualMachinePowerState.poweredOff, "off")]
    [InlineData(VirtualMachinePowerState.suspended, "error")]
    public async Task GetPowerState_MapsVcenterStates(VirtualMachinePowerState reported, string expected)
    {
        var vcenter = new FakeVcenter();
        vcenter.AddVm(VmA, reported);

        Assert.Equal(expected, await vcenter.Service().GetPowerState(VmA));
    }

    [Fact]
    public async Task GetPowerState_WhenTheMachineIsNotOnAnyConnection_ReportsError()
    {
        var vcenter = new FakeVcenter();

        Assert.Equal("error", await vcenter.Service().GetPowerState(VmA));
    }

    #endregion
}
