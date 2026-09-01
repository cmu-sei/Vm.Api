// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The requests ProxmoxService sends for the power commands and the cluster-wide reads, driven through
/// a substituted transport so no Proxmox cluster is involved. See <see cref="FakeProxmoxCluster"/> for
/// why the seam is the socket rather than the client.
/// </summary>
/// <remarks>
/// The vSphere counterpart of this class is <c>VsphereServiceCommandTests</c>, and the pairing is worth
/// keeping: the two drivers answer the same interface for the same UI gesture, and where they disagree
/// - vSphere prechecks the power state and swallows some failures, Proxmox does neither - the
/// difference is only visible with both pinned.
/// </remarks>
public class ProxmoxServiceCommandTests
{
    private const int Vmid = 100;

    #region Single power operations

    // Each operation, the route it is addressed to, and the string the caller is told. The routes are
    // the whole of what a power command is, and the strings reach the API response verbatim.
    [Theory]
    [InlineData(PowerOperation.PowerOn, "/status/start", "vmid 100 started")]
    [InlineData(PowerOperation.PowerOff, "/status/stop", "vmid 100 stopped")]
    [InlineData(PowerOperation.Reboot, "/status/reboot", "vmid 100 rebooted")]
    [InlineData(PowerOperation.Shutdown, "/status/shutdown", "vmid 100 shutdown")]
    public async Task PowerOperation_PostsToTheOperationsOwnRouteAndReportsWhatItSubmitted(
        PowerOperation operation, string route, string reported)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var path = FakeProxmoxCluster.VmPath(info, route);
        cluster.Accepts($"POST {path}");

        Assert.Equal(reported, await Submit(cluster.Service(), info, operation));

        // The only request made: a single power command trusts the stored node rather than resolving it,
        // unlike every path that goes through ResolveNode.
        Assert.Equal([path], cluster.Http.Paths);
    }

    // The same four operations against a container. Proxmox addresses containers under /lxc, and the
    // service picks the segment off ProxmoxVmInfo.Type - the one thing that distinguishes the two.
    [Theory]
    [InlineData(PowerOperation.PowerOn, "/status/start")]
    [InlineData(PowerOperation.PowerOff, "/status/stop")]
    [InlineData(PowerOperation.Reboot, "/status/reboot")]
    [InlineData(PowerOperation.Shutdown, "/status/shutdown")]
    public async Task PowerOperation_OnAContainer_IsAddressedUnderLxcRatherThanQemu(
        PowerOperation operation, string route)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);
        cluster.Accepts($"POST {FakeProxmoxCluster.VmPath(info, route)}");

        await Submit(cluster.Service(), info, operation);

        Assert.Equal([$"api2/json/nodes/pve1/lxc/{Vmid}{route}"], cluster.Http.Paths);
    }

    // Addressed to the node the caller believes the machine is on, which is what makes the stale-node
    // retry in BulkPowerOperation necessary - see ProxmoxServiceVmLookupTests.
    [Fact]
    public async Task PowerOn_IsAddressedToTheNodeTheCallerHolds()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, node: "pve7");
        cluster.Accepts($"POST {FakeProxmoxCluster.VmPath(info, "/status/start")}");

        await cluster.Service().PowerOnVm(info);

        Assert.Equal([$"api2/json/nodes/pve7/qemu/{Vmid}/status/start"], cluster.Http.Paths);
    }

    // The token goes out as PVEAPIToken with the configured value verbatim, and there is no login
    // handshake at all - one request per call. A deployment that misconfigures the token gets a 401
    // from Proxmox, which is only diagnosable if this is the shape actually sent.
    [Fact]
    public async Task PowerOn_AuthorizesWithTheConfiguredApiTokenAndNothingElse()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var path = FakeProxmoxCluster.VmPath(info, "/status/start");
        cluster.Accepts($"POST {path}");

        await cluster.Service().PowerOnVm(info);

        Assert.Equal(
            $"PVEAPIToken {FakeProxmoxCluster.ApiToken}",
            cluster.Request(HttpMethod.Post, path).Authorization);
    }

    // Every power command nudges the state poller once it has submitted, so the UI sees the new state
    // without waiting out the poll interval.
    [Theory]
    [InlineData(PowerOperation.PowerOn, "/status/start")]
    [InlineData(PowerOperation.PowerOff, "/status/stop")]
    [InlineData(PowerOperation.Reboot, "/status/reboot")]
    [InlineData(PowerOperation.Shutdown, "/status/shutdown")]
    public async Task PowerOperation_AsksTheStatePollerToRunAgain(PowerOperation operation, string route)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Accepts($"POST {FakeProxmoxCluster.VmPath(info, route)}");

        await Submit(cluster.Service(), info, operation);

        cluster.State.Received(1).CheckState();
    }

    // A refused submit throws rather than reporting a state, which is the difference from vSphere:
    // VsphereService answers "poweron submitted" whatever vCenter said, because its bulk path shares
    // this code. Proxmox has a separate bulk path, so the single-VM path can afford to fail loudly.
    [Fact]
    public async Task PowerOn_WhenProxmoxRefusesTheSubmit_Throws()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Rejects(
            $"POST {FakeProxmoxCluster.VmPath(info, "/status/start")}",
            "unable to start VM 100 - no such VM");

        await Assert.ThrowsAnyAsync<Exception>(() => cluster.Service().PowerOnVm(info));
    }

    // Unauthorized is the misconfigured-token case, and it has no errors object, so there is no message
    // to report - only the throw. Pinned so that a change making this quietly succeed is caught.
    [Fact]
    public async Task PowerOn_WhenProxmoxRejectsTheToken_Throws()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Http.Answers(
            $"POST {FakeProxmoxCluster.VmPath(info, "/status/start")}", HttpStatusCode.Unauthorized);

        await Assert.ThrowsAnyAsync<Exception>(() => cluster.Service().PowerOnVm(info));
    }

    #endregion

    #region Waiting for the Proxmox task

    // A submit Proxmox answers with a UPID has queued a task, and the single-VM commands wait for it
    // before reporting - so the string the caller gets means "finished", not "accepted". The two tests
    // in this region are the only ones that take the waiting path, because PveClient sleeps two seconds
    // before its first poll with no interval setting to turn that down.
    [Fact]
    public async Task PowerOn_WhenProxmoxQueuesATask_WaitsForItToFinishBeforeReporting()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.SubmitsTask($"POST {FakeProxmoxCluster.VmPath(info, "/status/start")}");

        Assert.Equal($"vmid {Vmid} started", await cluster.Service().PowerOnVm(info));

        // The task status really was read, which is what distinguishes waiting from not.
        Assert.Contains(cluster.Http.Paths, x => x.Contains("/tasks/") && x.EndsWith("/status"));
    }

    // A task that ran and failed is a failure of the command, even though the submit itself succeeded.
    // Nothing above this reads the task's exit status, so if this stopped throwing a failed power
    // operation would report success.
    [Fact]
    public async Task PowerOn_WhenTheQueuedTaskFails_Throws()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.SubmitsTask(
            $"POST {FakeProxmoxCluster.VmPath(info, "/status/start")}",
            exitStatus: "start failed: QEMU exited with code 1");

        await Assert.ThrowsAnyAsync<Exception>(() => cluster.Service().PowerOnVm(info));
    }

    #endregion

    #region GetVms and GetTasks

    // The cluster-wide read the state poller runs on every pass. Filtered to VMs by Proxmox itself
    // rather than client-side, so the query string is part of the contract.
    [Fact]
    public async Task GetVms_AsksProxmoxForVmResourcesAndMapsWhatItGetsBack()
    {
        var cluster = new FakeProxmoxCluster();
        cluster.Has(100, status: "running");
        cluster.Has(101, ProxmoxVmType.LXC, status: "stopped", node: "pve2");

        var vms = (await cluster.Service().GetVms()).ToList();

        Assert.Equal([100, 101], vms.Select(x => x.VmId).Order());
        Assert.Equal("pve2", vms.Single(x => x.VmId == 101).Node);
        Assert.True(vms.Single(x => x.VmId == 100).IsRunning);
        Assert.True(vms.Single(x => x.VmId == 101).IsStopped);
        Assert.Equal("?type=vm", cluster.Request(HttpMethod.Get, FakeProxmoxCluster.ClusterResources).Query);
    }

    // An empty cluster is a list with nothing in it, not a null and not a throw: the state poller runs
    // this on a timer and a deployment with no machines yet is normal.
    [Fact]
    public async Task GetVms_WhenTheClusterHasNoMachines_ComesBackEmpty()
    {
        var cluster = new FakeProxmoxCluster();

        Assert.Empty(await cluster.Service().GetVms());
    }

    // Cluster-wide rather than per-node, and regardless of which client submitted the task - which is
    // what lets ProxmoxTaskService observe power operations submitted by the bulk path.
    [Fact]
    public async Task GetTasks_ReadsTheClusterTaskListAndMapsTheFieldsTheTaskServiceUses()
    {
        var cluster = new FakeProxmoxCluster();
        cluster.Answers($"GET {FakeProxmoxCluster.ClusterTasks}", """
            [{"upid":"UPID:pve1:0000ABCD:0011:0022:qmstart:100:player@pve!vmapi:","node":"pve1",
              "type":"qmstart","id":"100","status":"OK","starttime":1700000000,"endtime":1700000005}]
            """);

        var task = Assert.Single(await cluster.Service().GetTasks());

        Assert.Equal("UPID:pve1:0000ABCD:0011:0022:qmstart:100:player@pve!vmapi:", task.UniqueTaskId);
        Assert.Equal("100", task.VmId);
        Assert.Equal("qmstart", task.Type);
        Assert.Equal("OK", task.Status);
    }

    // How ProxmoxTaskService tells a running task from a finished one: it reads Duration, which the
    // client derives from endtime, so a task with no end time is still running. Pinned here because
    // nothing in the task service's own code makes that mapping visible.
    [Fact]
    public async Task GetTasks_ATaskWithNoEndTimeHasNoDuration_WhichIsHowStillRunningIsSpelled()
    {
        var cluster = new FakeProxmoxCluster();
        cluster.Answers($"GET {FakeProxmoxCluster.ClusterTasks}", """
            [{"upid":"UPID:pve1:0000ABCD:0011:0022:qmstart:100:player@pve!vmapi:","node":"pve1",
              "type":"qmstart","id":"100","starttime":1700000000},
             {"upid":"UPID:pve1:0000ABCE:0011:0022:qmstop:101:player@pve!vmapi:","node":"pve1",
              "type":"qmstop","id":"101","status":"OK","starttime":1700000000,"endtime":1700000005}]
            """);

        var tasks = (await cluster.Service().GetTasks()).ToList();

        Assert.Null(tasks.Single(x => x.VmId == "100").Duration);
        Assert.NotNull(tasks.Single(x => x.VmId == "101").Duration);
    }

    #endregion

    #region GetNicOptions

    // Pure - no cluster is consulted at all. This is what the network dropdown in the VM UI is built
    // from, so what it contains and the order it is in are the whole of the feature.

    [Fact]
    public void GetNicOptions_ListsTheAllowedNetworksByTheirDisplayNames()
    {
        var options = Service().GetNicOptions(
            currentNetworks: new Dictionary<string, string> { ["net0"] = "vmbr1" },
            allowedNetworks: new Dictionary<string, string> { ["vmbr1"] = "Red Team", ["vmbr2"] = "Blue Team" },
            networkNames: null);

        Assert.Equal(new Dictionary<string, string> { ["vmbr1"] = "Red Team", ["vmbr2"] = "Blue Team" },
            options.AvailableNetworks);
        Assert.Empty(options.ReadOnlyNetworks);
    }

    // Ordered by what the user reads, not by the bridge id underneath it, so the dropdown is
    // alphabetical on screen.
    [Fact]
    public void GetNicOptions_OrdersByDisplayNameRatherThanBridgeId()
    {
        var options = Service().GetNicOptions(
            currentNetworks: null,
            allowedNetworks: new Dictionary<string, string>
            {
                ["vmbr1"] = "Zulu",
                ["vmbr2"] = "Alpha",
                ["vmbr3"] = "Mike",
            },
            networkNames: null);

        Assert.Equal(["Alpha", "Mike", "Zulu"], options.AvailableNetworks.Values);
    }

    // A network allowed but never named falls back to its bridge id, in both the label and the sort, so
    // a view that forgot to name a network still gets a usable dropdown rather than a blank entry.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetNicOptions_WhenAnAllowedNetworkHasNoName_LabelsItWithItsBridgeId(string name)
    {
        var options = Service().GetNicOptions(
            currentNetworks: null,
            allowedNetworks: new Dictionary<string, string> { ["vmbr1"] = name },
            networkNames: null);

        Assert.Equal("vmbr1", options.AvailableNetworks["vmbr1"]);
    }

    // The case the read-only list exists for: a machine already attached to a network its team is not
    // allowed to choose. Dropping it would make the dropdown show the wrong current selection, and
    // offering it would let the user re-select a network they cannot have, so it is listed and marked.
    [Fact]
    public void GetNicOptions_AddsANetworkTheVmIsOnButTheTeamCannotChoose_MarkedReadOnly()
    {
        var options = Service().GetNicOptions(
            currentNetworks: new Dictionary<string, string> { ["net0"] = "vmbr9" },
            allowedNetworks: new Dictionary<string, string> { ["vmbr1"] = "Red Team" },
            networkNames: null);

        Assert.Equal(["vmbr1", "vmbr9"], options.AvailableNetworks.Keys.Order());
        Assert.Equal(["vmbr9"], options.ReadOnlyNetworks);
    }

    // A read-only network is still labelled from the name map when one is available, so an
    // administrator sees "Uplink" rather than "vmbr9" for a network they cannot change.
    [Fact]
    public void GetNicOptions_LabelsAReadOnlyNetworkFromTheNameMapWhenItHasOne()
    {
        var options = Service().GetNicOptions(
            currentNetworks: new Dictionary<string, string> { ["net0"] = "vmbr9" },
            allowedNetworks: new Dictionary<string, string>(),
            networkNames: new Dictionary<string, string> { ["vmbr9"] = "Uplink" });

        Assert.Equal("Uplink", options.AvailableNetworks["vmbr9"]);
        Assert.Equal(["vmbr9"], options.ReadOnlyNetworks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetNicOptions_WhenAReadOnlyNetworkIsNamedBlank_FallsBackToItsBridgeId(string name)
    {
        var options = Service().GetNicOptions(
            currentNetworks: new Dictionary<string, string> { ["net0"] = "vmbr9" },
            allowedNetworks: new Dictionary<string, string>(),
            networkNames: new Dictionary<string, string> { ["vmbr9"] = name });

        Assert.Equal("vmbr9", options.AvailableNetworks["vmbr9"]);
    }

    // An adapter with no bridge - a NIC defined with no network attached - is not a network, so it is
    // neither offered nor marked read-only.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetNicOptions_IgnoresAnAdapterWithNoNetworkAttached(string bridge)
    {
        var options = Service().GetNicOptions(
            currentNetworks: new Dictionary<string, string> { ["net0"] = bridge },
            allowedNetworks: new Dictionary<string, string> { ["vmbr1"] = "Red Team" },
            networkNames: null);

        Assert.Equal(["vmbr1"], options.AvailableNetworks.Keys);
        Assert.Empty(options.ReadOnlyNetworks);
    }

    // A machine already on an allowed network is offered it once, not twice, and it is not read-only.
    [Fact]
    public void GetNicOptions_DoesNotDuplicateANetworkTheVmIsAlreadyAllowedToBeOn()
    {
        var options = Service().GetNicOptions(
            currentNetworks: new Dictionary<string, string> { ["net0"] = "vmbr1", ["net1"] = "vmbr1" },
            allowedNetworks: new Dictionary<string, string> { ["vmbr1"] = "Red Team" },
            networkNames: null);

        Assert.Equal(["vmbr1"], options.AvailableNetworks.Keys);
        Assert.Empty(options.ReadOnlyNetworks);
    }

    // All three inputs null: an empty answer rather than a null-reference, because the handler builds
    // these from a view's settings and any of them may be absent.
    [Fact]
    public void GetNicOptions_WithNothingSupplied_AnswersEmptyRatherThanThrowing()
    {
        var options = Service().GetNicOptions(null, null, null);

        Assert.Empty(options.AvailableNetworks);
        Assert.Empty(options.CurrentNetworks);
        Assert.Empty(options.ReadOnlyNetworks);
    }

    // Handed straight back rather than rebuilt, so the caller's adapter keys survive to the response.
    [Fact]
    public void GetNicOptions_ReportsTheCurrentNetworksItWasGiven()
    {
        var current = new Dictionary<string, string> { ["net0"] = "vmbr1", ["net1"] = "vmbr2" };

        var options = Service().GetNicOptions(
            current,
            new Dictionary<string, string> { ["vmbr1"] = "Red", ["vmbr2"] = "Blue" },
            null);

        Assert.Equal(current, options.CurrentNetworks);
    }

    #endregion

    /// <summary>
    /// The four single-VM power methods behind the operation they implement, so the routing and the
    /// reported string can be stated once per operation as a Theory.
    /// </summary>
    private static Task<string> Submit(IProxmoxService service, ProxmoxVmInfo info, PowerOperation operation) =>
        operation switch
        {
            PowerOperation.PowerOn => service.PowerOnVm(info),
            PowerOperation.PowerOff => service.PowerOffVm(info),
            PowerOperation.Reboot => service.RebootVm(info),
            PowerOperation.Shutdown => service.ShutdownVm(info),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    /// <summary>For the pure paths, which consult neither the cluster nor the database.</summary>
    private static IProxmoxService Service() => new FakeProxmoxCluster().Service();
}
