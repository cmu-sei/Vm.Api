// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The two <c>ProxmoxService</c> methods that read the database: <c>GetCurrentNodeForVm</c>, which
/// answers "where does this Vm live right now", and <c>BulkPowerOperation</c>, which turns a
/// multi-select power gesture into one submit per Vm and a per-Vm message back.
///
/// This is the one Proxmox driver class that needs a database, and that is a statement about the driver
/// rather than about the test: every other method takes the <c>ProxmoxVmInfo</c> it needs as an argument,
/// so <see cref="FakeProxmoxCluster.Service"/> hands them a null context on purpose. These two look the
/// machine up themselves, which is why the lookup - a Vm with no Proxmox info, an id no row matches, a
/// stored node the cluster has moved out from under - is as much of their behaviour as the requests they
/// then send.
/// </summary>
/// <remarks>
/// <para>
/// <c>BulkPowerOperationEndpointTests</c> already pins the per-Vm dictionary contract over the wire, but
/// with <c>IProxmoxService</c> substituted, so what it shows is that the endpoint relays a dictionary -
/// not that this driver produces the right one. Everything below drives the real driver against a
/// substituted transport (<see cref="FakeProxmoxCluster"/>) and a real PostgreSQL database.
/// </para>
/// <para>
/// The vSphere mirror is <c>VsphereServiceCommandTests</c>' BulkPowerOperation region, and the two
/// drivers disagree in ways only visible with both pinned: they share the "Virtual machine not found"
/// string for an id they cannot resolve, but an operation the driver does not handle is
/// "Unsupported Operation" in vSphere and "&lt;operation&gt; is not supported on Proxmox virtual
/// machines." here; and vSphere swallows a rejected submit as success while Proxmox reports it - except
/// where the refusal carries no <c>errors</c> object, or the node cannot be reached at all, both of which
/// Proxmox reports as success too. The last two are characterized below.
/// </para>
/// <para>
/// <c>BulkPowerOperation</c> fans out with <c>Task.WhenAll</c>, so requests for different Vms arrive in a
/// non-deterministic order. Multi-Vm assertions here are per-path counts, never an ordered
/// <c>Http.Paths</c>.
/// </para>
/// </remarks>
public class ProxmoxServiceVmLookupTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private const int Vmid = 100;

    /// <summary>What Proxmox says about a submit it refuses, as the <c>errors</c> object carries it.</summary>
    private const string Refusal = "unable to start VM 100 - no such VM";

    /// <summary>
    /// <c>Result.GetError()</c> renders each entry of the <c>errors</c> object as "field : message", and
    /// a Proxmox power refusal has no field - so the string the caller is handed keeps the separator.
    /// </summary>
    private const string ReportedRefusal = " : " + Refusal;

    // Ordered so that a test can name them in a stable sequence when it needs to.
    private static readonly Guid VmA = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid VmB = new("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid VmC = new("cccccccc-0000-4000-8000-000000000003");

    #region GetCurrentNodeForVm

    // A Vm that is not a Proxmox Vm at all answers null without a single request going out. Callers use
    // this to decide whether a Proxmox-specific feature applies, so a vSphere Vm reaching the cluster
    // would be both a wasted round trip and a lie about what the answer means.
    [Fact]
    public async Task GetCurrentNodeForVm_ForAVmWithNoProxmoxInfo_ReportsNothingAndAsksProxmoxNothing()
    {
        var cluster = new FakeProxmoxCluster();
        await Seed(new VmEntity { Id = VmA, Name = "vsphere-vm", Type = VmType.Vsphere });

        Assert.Null(await cluster.Service(Db).GetCurrentNodeForVm(VmA, Ct));

        Assert.Empty(cluster.Http.Sent);
    }

    // An id with no row behind it is the same answer, not a throw: the id reaches this from a caller that
    // may be racing a deleted Vm.
    [Fact]
    public async Task GetCurrentNodeForVm_ForAnIdWithNoVmRow_ReportsNothingAndAsksProxmoxNothing()
    {
        var cluster = new FakeProxmoxCluster();

        Assert.Null(await cluster.Service(Db).GetCurrentNodeForVm(Guid.NewGuid(), Ct));

        Assert.Empty(cluster.Http.Sent);
    }

    /// <summary>
    /// A Proxmox Vm the cluster knows answers the node the cluster reports, from one cluster-wide read.
    /// </summary>
    /// <remarks>
    /// Also the proof that the <c>ProxmoxVmInfo</c> navigation is visible to a query with no
    /// <c>Include</c>: the query at <c>ProxmoxService.cs:128</c> has none, and what makes it work is
    /// <c>VmConfiguration</c>'s <c>Navigation(x =&gt; x.ProxmoxVmInfo).AutoInclude()</c>. Removing that
    /// one line turns every Proxmox Vm into the "not a Proxmox Vm" case above, silently, so this test is
    /// what stands under it.
    /// </remarks>
    [Fact]
    public async Task GetCurrentNodeForVm_ForAProxmoxVm_ReportsTheNodeTheClusterRunsItOn()
    {
        var cluster = new FakeProxmoxCluster();
        await SeedVm(cluster.Has(Vmid, node: "pve3", vmId: VmA));

        Assert.Equal("pve3", await cluster.Service(Db).GetCurrentNodeForVm(VmA, Ct));

        Assert.Equal([FakeProxmoxCluster.ClusterResources], cluster.Http.Paths);
        Assert.Equal(
            "?type=vm", cluster.Request(HttpMethod.Get, FakeProxmoxCluster.ClusterResources).Query);
    }

    /// <summary>
    /// A migrated Vm answers the node it moved to, which is the whole reason this resolves rather than
    /// reading the stored node - but the refreshed node is never written back.
    /// </summary>
    /// <remarks>
    /// Characterizing, not endorsing. <c>IProxmoxService</c>'s doc comment says this "also refreshes the
    /// stored node", and <c>ResolveNode</c> does assign <c>info.Node</c> - but on an instance from an
    /// <c>AsNoTracking</c> query, and nothing calls <c>SaveChanges</c> on any path, so the row keeps the
    /// stale node until the state poller happens to update it. The re-read below is the assertion: it
    /// will redden the day a save is added, which is the day the doc comment becomes true.
    /// </remarks>
    [Fact]
    public async Task GetCurrentNodeForVm_AfterAMigration_ReportsTheNewNodeButLeavesTheStoredNodeStale()
    {
        var cluster = new FakeProxmoxCluster();
        await SeedVm(cluster.Has(Vmid, node: "pve1", vmId: VmA));
        cluster.Migrates(Vmid, "pve2");

        Assert.Equal("pve2", await cluster.Service(Db).GetCurrentNodeForVm(VmA, Ct));

        await using var context = NewContext();
        Assert.Equal("pve1", (await context.ProxmoxVmInfo.SingleAsync(x => x.VmId == VmA, Ct)).Node);
    }

    // A row that names a vmid the cluster has never heard of is a real error rather than "no node":
    // answering null would be indistinguishable from a vSphere Vm and would leave the caller acting on a
    // machine that does not exist. The vmid is in the message because that is the only thing an operator
    // can search Proxmox for.
    [Fact]
    public async Task GetCurrentNodeForVm_WhenTheClusterDoesNotKnowTheVmid_Throws()
    {
        var cluster = new FakeProxmoxCluster();
        await SeedVm(new ProxmoxVmInfo { VmId = VmA, Id = Vmid, Node = "pve1" });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.Service(Db).GetCurrentNodeForVm(VmA, Ct));

        Assert.Equal($"Could not find vmid {Vmid} in Proxmox", exception.Message);
    }

    // Resolving hands what the cluster said to the state poller, once, so a power state observed on the
    // way past is not thrown away. This is the only side effect the lookup has.
    [Fact]
    public async Task GetCurrentNodeForVm_HandsTheResolvedMachineToTheStatePollerOnce()
    {
        var cluster = new FakeProxmoxCluster();
        await SeedVm(cluster.Has(Vmid, vmId: VmA));

        await cluster.Service(Db).GetCurrentNodeForVm(VmA, Ct);

        await cluster.State.Received(1).UpdateVm(Arg.Any<IClusterResourceVm>());
    }

    #endregion

    #region BulkPowerOperation

    /// <summary>
    /// An empty selection answers an empty dictionary without reading the database or the cluster.
    /// </summary>
    /// <remarks>
    /// The service is built with a null context deliberately: that is what makes "no database read"
    /// assertable at all. Delete the short circuit and this fails with a
    /// <see cref="NullReferenceException"/> rather than passing quietly on an empty query.
    /// </remarks>
    [Fact]
    public async Task BulkPowerOperation_WithNoIds_ReportsNothingAndTouchesNeitherTheDatabaseNorTheCluster()
    {
        var cluster = new FakeProxmoxCluster();

        Assert.Empty(await cluster.Service().BulkPowerOperation([], PowerOperation.PowerOn));

        Assert.Empty(cluster.Http.Sent);
    }

    // The mixed case this method exists for: one id the database cannot resolve, reported against that id
    // and that id only, while every other id in the same call still gets its command submitted. The
    // string is verbatim the one vSphere uses - see
    // VsphereServiceCommandTests.BulkPowerOperation_WhenAMachineIsNotOnAnyConnection_SaysSoForThatMachineOnly
    // - so a UI showing a mixed result does not have to know which driver answered.
    [Fact]
    public async Task BulkPowerOperation_ForAnIdWithNoProxmoxInfo_SaysSoForThatIdAndStillSubmitsTheRest()
    {
        var cluster = new FakeProxmoxCluster();
        var healthy = cluster.Has(Vmid, vmId: VmB);
        await SeedVm(healthy);
        var path = FakeProxmoxCluster.VmPath(healthy, "/status/start");
        cluster.Accepts($"POST {path}");

        var results = await cluster.Service(Db).BulkPowerOperation([VmA, VmB], PowerOperation.PowerOn);

        Assert.Equal("Virtual machine not found", results[VmA]);
        Assert.Equal(string.Empty, results[VmB]);

        // Nothing was addressed to the unresolvable id - it has no vmid to address.
        Assert.Equal([path], cluster.Http.Paths);
    }

    // The four operations against both guest types, which is the whole of what a bulk submit is. The
    // single-Vm routes are pinned in ProxmoxServiceCommandTests; these are the bulk path's own, and they
    // are separate code - the two paths share only SubmitPowerOperation.
    [Theory]
    [InlineData(PowerOperation.PowerOn, ProxmoxVmType.QEMU, "api2/json/nodes/pve1/qemu/100/status/start")]
    [InlineData(PowerOperation.PowerOff, ProxmoxVmType.QEMU, "api2/json/nodes/pve1/qemu/100/status/stop")]
    [InlineData(PowerOperation.Reboot, ProxmoxVmType.QEMU, "api2/json/nodes/pve1/qemu/100/status/reboot")]
    [InlineData(PowerOperation.Shutdown, ProxmoxVmType.QEMU, "api2/json/nodes/pve1/qemu/100/status/shutdown")]
    [InlineData(PowerOperation.PowerOn, ProxmoxVmType.LXC, "api2/json/nodes/pve1/lxc/100/status/start")]
    [InlineData(PowerOperation.PowerOff, ProxmoxVmType.LXC, "api2/json/nodes/pve1/lxc/100/status/stop")]
    [InlineData(PowerOperation.Reboot, ProxmoxVmType.LXC, "api2/json/nodes/pve1/lxc/100/status/reboot")]
    [InlineData(PowerOperation.Shutdown, ProxmoxVmType.LXC, "api2/json/nodes/pve1/lxc/100/status/shutdown")]
    public async Task BulkPowerOperation_PostsEachOperationToItsOwnRouteForThatGuestType(
        PowerOperation operation, ProxmoxVmType type, string expected)
    {
        var cluster = new FakeProxmoxCluster();
        await SeedVm(cluster.Has(Vmid, type, vmId: VmA));
        cluster.Accepts($"POST {expected}");

        var results = await cluster.Service(Db).BulkPowerOperation([VmA], operation);

        // An accepted submit is reported as an empty string, not as a message: the dictionary is read as
        // "errors by Vm", so anything non-empty turns that Vm red in the UI.
        Assert.Equal(string.Empty, results[VmA]);

        // One request, and it is the submit. The happy path never resolves the node, so a bulk power on
        // over fifty Vms costs fifty requests rather than a hundred.
        Assert.Equal([expected], cluster.Http.Paths);
        Assert.Equal("{}", cluster.Request(HttpMethod.Post, expected).Body);
    }

    /// <summary>
    /// Revert has no case in the switch, and the message that reaches the caller is the
    /// <see cref="NotSupportedException"/>'s - turned into a per-Vm entry rather than thrown out of the
    /// call.
    /// </summary>
    /// <remarks>
    /// Reachable, not theoretical: <c>PowerOperation.Revert</c> is a value of the enum the API binds, and
    /// vSphere handles it here by reverting to the current snapshot. So the same request that reverts a
    /// vSphere Vm answers this for a Proxmox one - and with a different string again ("Unsupported
    /// Operation" over there), which is the kind of divergence only a pair of pinned drivers shows.
    /// </remarks>
    [Fact]
    public async Task BulkPowerOperation_ForRevert_ReportsThatProxmoxDoesNotSupportItRatherThanThrowing()
    {
        var cluster = new FakeProxmoxCluster();
        await SeedVm(cluster.Has(Vmid, vmId: VmA));

        var results = await cluster.Service(Db).BulkPowerOperation([VmA], PowerOperation.Revert);

        Assert.Equal("Revert is not supported on Proxmox virtual machines.", results[VmA]);

        // The throw happens before any request is built, so the cluster hears nothing at all.
        Assert.Empty(cluster.Http.Sent);
    }

    // The stale-node retry, which is what makes a bulk power operation survive a migration. The stored
    // node is only refreshed by the state poller, so between a migration and the next poll every submit
    // is addressed to a node the machine has left; Proxmox refuses it, the node is re-resolved, and the
    // second submit goes to where the machine now is.
    [Fact]
    public async Task BulkPowerOperation_WhenTheStoredNodeIsStale_ResubmitsToTheNodeTheClusterNowReports()
    {
        var cluster = new FakeProxmoxCluster();
        await SeedVm(cluster.Has(Vmid, node: "pve1", vmId: VmA));
        cluster.Migrates(Vmid, "pve2");
        var stale = FakeProxmoxCluster.VmPath("pve1", ProxmoxVmType.QEMU, Vmid, "/status/start");
        var live = FakeProxmoxCluster.VmPath("pve2", ProxmoxVmType.QEMU, Vmid, "/status/start");
        cluster.Rejects($"POST {stale}", Refusal);
        cluster.Accepts($"POST {live}");

        var results = await cluster.Service(Db).BulkPowerOperation([VmA], PowerOperation.PowerOn);

        // The retry succeeded, so the caller is told nothing went wrong - the first refusal is not
        // reported, which is the point.
        Assert.Equal(string.Empty, results[VmA]);
        Assert.Single(cluster.Requests(HttpMethod.Post, stale));
        Assert.Single(cluster.Requests(HttpMethod.Post, live));
        Assert.Equal(2, cluster.Http.Sent.Count(x => x.Method == HttpMethod.Post));
    }

    // Once, not until it works. A retry loop here would multiply a cluster-wide outage by however many
    // Vms are selected, against a Proxmox that is already refusing, so the second failure is reported
    // rather than retried.
    [Fact]
    public async Task BulkPowerOperation_WhenTheResubmitToTheNewNodeAlsoFails_ReportsItWithoutAThirdAttempt()
    {
        var cluster = new FakeProxmoxCluster();
        await SeedVm(cluster.Has(Vmid, node: "pve1", vmId: VmA));
        cluster.Migrates(Vmid, "pve2");
        var stale = FakeProxmoxCluster.VmPath("pve1", ProxmoxVmType.QEMU, Vmid, "/status/start");
        var live = FakeProxmoxCluster.VmPath("pve2", ProxmoxVmType.QEMU, Vmid, "/status/start");
        cluster.Rejects($"POST {stale}", Refusal);
        cluster.Rejects($"POST {live}", Refusal);

        var results = await cluster.Service(Db).BulkPowerOperation([VmA], PowerOperation.PowerOn);

        Assert.Equal(ReportedRefusal, results[VmA]);
        Assert.Single(cluster.Requests(HttpMethod.Post, stale));
        Assert.Single(cluster.Requests(HttpMethod.Post, live));
        Assert.Equal(2, cluster.Http.Sent.Count(x => x.Method == HttpMethod.Post));
    }

    // A refusal that has nothing to do with the node is retried all the same - the service cannot tell
    // the two apart from a Result - and then reported. Twice at most, on the same route.
    [Fact]
    public async Task BulkPowerOperation_WhenTheSubmitFailsAndTheNodeHasNotChanged_TriesTwiceThenReportsWhatProxmoxSaid()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, vmId: VmA);
        await SeedVm(info);
        var path = FakeProxmoxCluster.VmPath(info, "/status/start");
        cluster.Rejects($"POST {path}", Refusal);

        var results = await cluster.Service(Db).BulkPowerOperation([VmA], PowerOperation.PowerOn);

        Assert.Equal(ReportedRefusal, results[VmA]);
        Assert.Equal(2, cluster.Requests(HttpMethod.Post, path).Count);
    }

    // A vmid the cluster no longer knows cannot be re-resolved, so there is nothing to retry against and
    // the first refusal is what the caller is told. Retrying the same stale node would double the cost
    // of a bulk operation over Vms that have been deleted out from under this database.
    [Fact]
    public async Task BulkPowerOperation_WhenTheClusterDoesNotKnowTheVmid_ReportsTheRefusalWithoutRetrying()
    {
        var cluster = new FakeProxmoxCluster();
        var info = new ProxmoxVmInfo { VmId = VmA, Id = Vmid, Node = "pve1" };
        await SeedVm(info);
        var path = FakeProxmoxCluster.VmPath(info, "/status/start");
        cluster.Rejects($"POST {path}", Refusal);

        var results = await cluster.Service(Db).BulkPowerOperation([VmA], PowerOperation.PowerOn);

        Assert.Equal(ReportedRefusal, results[VmA]);
        Assert.Single(cluster.Requests(HttpMethod.Post, path));

        // The node was looked for - the resolve happened and came back empty, which is what stopped the
        // retry rather than the retry never being attempted.
        Assert.Single(cluster.Requests(HttpMethod.Get, FakeProxmoxCluster.ClusterResources));
    }

    /// <summary>
    /// A submit Proxmox refuses without an <c>errors</c> object is reported as an empty string - the same
    /// thing success is reported as.
    /// </summary>
    /// <remarks>
    /// Characterizing a real defect, not a desirable behaviour. <c>Result.GetError()</c> is built only
    /// from an <c>errors</c> object in the body, and a 401 from a misconfigured token or a 502 from a
    /// gateway in front of Proxmox carries none - so both submits below failed, and the caller is told the
    /// power operation was accepted. The two POSTs are the assertion that it really did fail twice; if
    /// this is ever fixed to report a status or a body, the first assertion reddens and should be updated
    /// rather than restored.
    /// </remarks>
    [Fact]
    public async Task BulkPowerOperation_WhenARefusalCarriesNoErrorDetail_ReportsAnEmptyStringJustLikeSuccess()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, vmId: VmA);
        await SeedVm(info);
        var path = FakeProxmoxCluster.VmPath(info, "/status/start");
        cluster.Http.AnswersJson(
            $"POST {path}",
            FakeProxmoxCluster.Data("null"),
            HttpStatusCode.InternalServerError);

        var results = await cluster.Service(Db).BulkPowerOperation([VmA], PowerOperation.PowerOn);

        Assert.Equal(string.Empty, results[VmA]);
        Assert.Equal(2, cluster.Requests(HttpMethod.Post, path).Count);
    }

    /// <summary>
    /// A node that cannot be reached at all - a refused connection rather than a refusal - is reported as
    /// an empty string, the same thing success is reported as, while the rest of the selection is
    /// submitted normally.
    /// </summary>
    /// <remarks>
    /// Characterizing a defect, and the worse face of the one below. <c>PveClient</c> catches the
    /// transport exception itself and answers an unsuccessful <c>Result</c> rather than letting it reach
    /// the per-Vm catch at <c>ProxmoxService.cs:481</c> - the two POSTs below are the proof, since an
    /// exception reaching that catch would have skipped the stale-node retry - and that Result carries no
    /// <c>errors</c> object, so <c>GetError()</c> is empty. A node that is down is therefore reported to
    /// the UI as a power operation that was accepted. The vSphere driver reaches its catch and reports the
    /// fault message; see
    /// <c>VsphereServiceCommandTests.BulkPowerOperation_WhenOneMachineIsRejected_ReportsThatOneAndStillReportsTheRest</c>.
    /// </remarks>
    [Fact]
    public async Task BulkPowerOperation_WhenAVmsNodeIsUnreachable_ReportsItAsSuccessButStillSubmitsTheRest()
    {
        var cluster = new FakeProxmoxCluster();
        var failing = cluster.Has(Vmid, vmId: VmA);
        var healthy = cluster.Has(101, vmId: VmB);
        await SeedVm(failing);
        await SeedVm(healthy);
        var failingPath = FakeProxmoxCluster.VmPath(failing, "/status/stop");
        var healthyPath = FakeProxmoxCluster.VmPath(healthy, "/status/stop");
        cluster.Http.Throws($"POST {failingPath}");
        cluster.Accepts($"POST {healthyPath}");

        var results = await cluster.Service(Db).BulkPowerOperation([VmA, VmB], PowerOperation.PowerOff);

        // Indistinguishable from the Vm that really was accepted.
        Assert.Equal(string.Empty, results[VmA]);
        Assert.Equal(string.Empty, results[VmB]);

        // The unreachable Vm went through the whole failure path - submit, re-resolve, resubmit - and
        // still produced the success string, while the healthy Vm was submitted once.
        Assert.Equal(2, cluster.Requests(HttpMethod.Post, failingPath).Count);
        Assert.Single(cluster.Requests(HttpMethod.Get, FakeProxmoxCluster.ClusterResources));
        Assert.Single(cluster.Requests(HttpMethod.Post, healthyPath));
    }

    // One nudge for the whole batch, after every submit has been attempted. Per-Vm it would be one
    // needless poll per Vm in a multi-select; and it is outside the per-Vm catch, so a batch in which
    // everything failed still refreshes what the UI shows.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BulkPowerOperation_AsksTheStatePollerToRunAgainOnceForTheWholeBatch(bool accepted)
    {
        var cluster = new FakeProxmoxCluster();
        var first = cluster.Has(Vmid, vmId: VmA);
        var second = cluster.Has(101, vmId: VmB);
        await SeedVm(first);
        await SeedVm(second);
        var firstPath = FakeProxmoxCluster.VmPath(first, "/status/start");
        var secondPath = FakeProxmoxCluster.VmPath(second, "/status/start");

        if (accepted)
        {
            cluster.Accepts($"POST {firstPath}").Accepts($"POST {secondPath}");
        }
        else
        {
            cluster.Rejects($"POST {firstPath}", Refusal).Rejects($"POST {secondPath}", Refusal);
        }

        await cluster.Service(Db).BulkPowerOperation([VmA, VmB], PowerOperation.PowerOn);

        cluster.State.Received(1).CheckState();
    }

    // Every id given is a key in what comes back, whatever happened to it. The caller matches the
    // dictionary against the selection it sent, so an id silently dropped is a Vm whose outcome the UI
    // never shows - which is the bug the "Virtual machine not found" fill-in was added for.
    [Fact]
    public async Task BulkPowerOperation_ReportsAnOutcomeForEveryIdItWasGiven()
    {
        var cluster = new FakeProxmoxCluster();
        var accepted = cluster.Has(Vmid, vmId: VmA);
        var refused = cluster.Has(101, vmId: VmB);
        await SeedVm(accepted);
        await SeedVm(refused);
        cluster.Accepts($"POST {FakeProxmoxCluster.VmPath(accepted, "/status/start")}");
        cluster.Rejects($"POST {FakeProxmoxCluster.VmPath(refused, "/status/start")}", Refusal);

        var results = await cluster.Service(Db)
            .BulkPowerOperation([VmA, VmB, VmC], PowerOperation.PowerOn);

        Assert.Equal(3, results.Count);
        Assert.Equal(string.Empty, results[VmA]);
        Assert.Equal(ReportedRefusal, results[VmB]);
        Assert.Equal("Virtual machine not found", results[VmC]);
    }

    #endregion

    /// <summary>
    /// A saved Vm row for the machine <paramref name="info"/> describes, with the info attached - which
    /// is the only shape either method under test can see, since <c>ProxmoxVmInfo</c> is keyed to a Vm.
    /// </summary>
    private Task SeedVm(ProxmoxVmInfo info) =>
        Seed(new VmEntity
        {
            Id = info.VmId,
            Name = $"proxmox-{info.Id}",
            Type = VmType.Proxmox,
            ProxmoxVmInfo = info,
        });
}
