// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.CSharp.RuntimeBinder;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The four snapshot operations of <see cref="ProxmoxService"/>: the request each one sends, the string
/// each one reports, and what <c>GetSnapshots</c> makes of the list Proxmox answers with. Driven through
/// a substituted transport, so the client's route building, its parameter serialization and its task
/// waiting all run for real - see <see cref="FakeProxmoxCluster"/> for why the seam is the socket.
/// </summary>
/// <remarks>
/// <para>
/// Three things in this region are invisible from the <c>IProxmoxService</c> signatures and are the
/// reason the class exists. A revert is the only snapshot operation that nudges the state poller, because
/// it is the only one that can change the power state; a container create silently discards the
/// <c>includeRam</c> flag its caller passed; and a delete goes out as <c>DELETE</c> on the snapshot's own
/// route despite the client method being called <c>Delsnapshot</c>. Each is one edit away from being
/// wrong in a way no compiler and no endpoint test would notice.
/// </para>
/// <para>
/// The other reason is the defect in the region below: <c>GetSnapshots</c> reads its optional fields
/// through <c>!= null</c> guards, which is not how an absent key behaves on the <c>ExpandoObject</c> the
/// response actually arrives as. Those tests are characterizations - see their remarks.
/// </para>
/// </remarks>
public class ProxmoxServiceSnapshotTests
{
    private const int Vmid = 100;
    private const string Snap = "snap1";

    /// <summary>Every optional field present, which is the only shape <c>GetSnapshots</c> survives.</summary>
    private const string OneSnapshot =
        """[{"name":"snap1","description":"before patching","parent":"snap0","vmstate":1,"snaptime":1700000000}]""";

    #region GetSnapshots

    // One route per machine type, reached through two differently named client methods - SnapshotList()
    // for a VM and List() for a container - that happen to build the same path. The type is the only
    // thing that picks between them, so both are pinned.
    [Theory]
    [InlineData(ProxmoxVmType.QEMU, "qemu")]
    [InlineData(ProxmoxVmType.LXC, "lxc")]
    public async Task GetSnapshots_ReadsTheSnapshotRouteForTheMachinesOwnType(
        ProxmoxVmType type, string segment)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, type);
        cluster.Answers($"GET {SnapshotPath(info)}", "[]");

        await cluster.Service().GetSnapshots(info);

        Assert.Equal([$"api2/json/nodes/pve1/{segment}/{Vmid}/snapshot"], cluster.Http.Paths);
    }

    // Every field the snapshot list carries reaches the model, which is the whole of what the snapshot
    // panel in the UI is drawn from: the parent is what makes it a tree and the snaptime is what orders it.
    [Fact]
    public async Task GetSnapshots_MapsEveryFieldProxmoxReportsForASnapshot()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {SnapshotPath(info)}", OneSnapshot);

        var snapshot = Assert.Single(await cluster.Service().GetSnapshots(info));

        Assert.Equal("snap1", snapshot.Name);
        Assert.Equal("before patching", snapshot.Description);
        Assert.Equal("snap0", snapshot.Parent);
        Assert.True(snapshot.VmState);
        Assert.Equal(1700000000L, snapshot.SnapTime);
    }

    // vmstate is a Proxmox flag, not a JSON bool: it arrives as 1 for a snapshot that captured RAM and 0
    // for one that did not, and only the 1 is true. A snapshot with RAM is the one a revert can restore
    // without a reboot, so the distinction is what the caller acts on.
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("null", false)]
    public async Task GetSnapshots_ReportsVmStateOnlyWhenProxmoxSaysOne(string vmstate, bool expected)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers(
            $"GET {SnapshotPath(info)}",
            $$"""[{"name":"snap1","description":null,"parent":null,"vmstate":{{vmstate}},"snaptime":null}]""");

        var snapshot = Assert.Single(await cluster.Service().GetSnapshots(info));

        Assert.Equal(expected, snapshot.VmState);
    }

    // An entry with no usable name is dropped rather than surfaced as a nameless snapshot: the name is the
    // only handle the revert and delete routes have, so one without it could only be displayed, not used.
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task GetSnapshots_SkipsAnEntryWithNoUsableName(string name)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers(
            $"GET {SnapshotPath(info)}",
            $$"""
            [{"name":{{name}},"description":null,"parent":null,"vmstate":null,"snaptime":null},
             {"name":"snap1","description":null,"parent":null,"vmstate":null,"snaptime":null}]
            """);

        var snapshots = await cluster.Service().GetSnapshots(info);

        Assert.Equal(["snap1"], snapshots.Select(x => x.Name));
    }

    // The synthetic "current" entry Proxmox appends to every snapshot list is deliberately surfaced rather
    // than filtered, so a caller can tell which snapshot the machine is running from - the Proxmox UI
    // lists it the same way. Note the arrangement has to spell the optional fields as explicit nulls; the
    // entry Proxmox really sends omits them, and that throws - see GetSnapshots_OnTheCurrentEntry... below.
    [Fact]
    public async Task GetSnapshots_SurfacesTheSyntheticCurrentEntryRatherThanFilteringIt()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers(
            $"GET {SnapshotPath(info)}",
            """
            [{"name":"snap1","description":"before patching","parent":null,"vmstate":1,"snaptime":1700000000},
             {"name":"current","description":"You are here!","parent":"snap1","vmstate":null,"snaptime":null}]
            """);

        var snapshots = await cluster.Service().GetSnapshots(info);

        Assert.Equal(["snap1", "current"], snapshots.Select(x => x.Name));
    }

    // A machine with no snapshots reads as an empty list, not a null and not a throw: the snapshot panel
    // opens on every VM, and having taken none is the normal case.
    [Fact]
    public async Task GetSnapshots_WhenTheMachineHasNoSnapshots_ComesBackEmpty()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {SnapshotPath(info)}", "[]");

        Assert.Empty(await cluster.Service().GetSnapshots(info));
    }

    // A refused read throws carrying both the vmid and whatever Proxmox said about it. The message is the
    // only diagnostic a caller gets - the endpoint turns it into a 500 body - so it is worth pinning that
    // the Proxmox error survives into it rather than being replaced by a generic sentence. Note the
    // contrast with GetConsole, which throws GetError() on its own: GetError() reads only an "errors"
    // object, so on a bare 401 or 502 that produces an exception with an empty message, while this one
    // still says which vmid failed doing what. Wrapping it is the difference.
    [Fact]
    public async Task GetSnapshots_WhenProxmoxRefusesTheRead_ThrowsCarryingWhatProxmoxReported()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Rejects($"GET {SnapshotPath(info)}", "VM 100 does not exist");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => cluster.Service().GetSnapshots(info));

        Assert.Contains($"SnapshotList failed for vmid={Vmid}", error.Message);
        Assert.Contains("VM 100 does not exist", error.Message);
    }

    #endregion

    #region GetSnapshots and the fields Proxmox omits

    /// <remarks>
    /// <para>
    /// CHARACTERIZATION OF A DEFECT, NOT THE INTENDED BEHAVIOUR. <c>GetSnapshots</c> reads its optional
    /// fields as <c>d.description != null ? ... : null</c>, which reads as a tolerance for a field
    /// Proxmox did not send. It is not one. <c>Result.ToData()</c> hands out an
    /// <c>ExpandoObject</c> per entry, and a dynamic read of a key an <c>ExpandoObject</c> does not
    /// contain throws <see cref="RuntimeBinderException"/> rather than answering null - so those guards
    /// only work when the key is present and the value is JSON <c>null</c>.
    /// </para>
    /// <para>
    /// That matters because of the very entry the test above exists for. The synthetic entry Proxmox
    /// appends is <c>{"name":"current","digest":"..."}</c> - no <c>description</c>, no <c>parent</c>, no
    /// <c>vmstate</c>, no <c>snaptime</c> - and it is present in the response for any machine that has
    /// snapshots. So the read a real cluster answers is the read this method throws on: the snapshot
    /// panel fails with a runtime binder error where it means to return a list, and it fails precisely
    /// once the machine has something worth listing.
    /// </para>
    /// <para>
    /// A fix reads each key off the <c>IDictionary&lt;string, object&gt;</c> that an <c>ExpandoObject</c>
    /// also is, through <c>TryGetValue</c>. That has been tried against these tests: it compiles - which
    /// is itself the confirmation of what the entries are - and it turns every test in this region red
    /// while leaving every other test in this class green. WHEN THAT LANDS THIS TEST GOES RED, AND THAT
    /// IS THE POINT: it is asserting the bug, so its failure is the confirmation that the bug is gone.
    /// Replace it then with an assertion that "current" is listed with null fields.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetSnapshots_OnTheCurrentEntryProxmoxActuallySends_ThrowsInsteadOfListing()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers(
            $"GET {SnapshotPath(info)}",
            """
            [{"name":"snap1","description":"before patching","parent":null,"vmstate":1,"snaptime":1700000000},
             {"name":"current","digest":"5f2b0e5c8e9d4a1b8c7d6e5f4a3b2c1d0e9f8a7b"}]
            """);

        await Assert.ThrowsAnyAsync<RuntimeBinderException>(
            () => cluster.Service().GetSnapshots(info));
    }

    /// <remarks>
    /// The same defect from the other direction: any one omitted field is enough, so a named snapshot
    /// taken with no description - which is what <c>qm snapshot 100 snap1</c> with no <c>--description</c>
    /// produces - is also unlistable. Each row here is one absent key. See
    /// <see cref="GetSnapshots_OnTheCurrentEntryProxmoxActuallySends_ThrowsInsteadOfListing"/> for why
    /// this reddens when the bug is fixed.
    /// </remarks>
    [Theory]
    [InlineData("description")]
    [InlineData("parent")]
    [InlineData("vmstate")]
    [InlineData("snaptime")]
    public async Task GetSnapshots_WhenProxmoxOmitsAnOptionalField_ThrowsRatherThanReadingItAsAbsent(
        string omitted)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {SnapshotPath(info)}", $"[{{{Fields(omitted)}}}]");

        await Assert.ThrowsAnyAsync<RuntimeBinderException>(
            () => cluster.Service().GetSnapshots(info));
    }

    /// <remarks>
    /// And on the one field that is guarded for a reason: the nameless-entry skip at the top of the loop
    /// only fires for a <c>name</c> that is present and null or empty. An entry carrying no <c>name</c>
    /// key throws before the skip can drop it, so the guard does not defend against the response shape it
    /// looks like it defends against. Characterization; reddens when the defect is fixed.
    /// </remarks>
    [Fact]
    public async Task GetSnapshots_WhenAnEntryCarriesNoNameKeyAtAll_ThrowsRatherThanBeingSkipped()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {SnapshotPath(info)}", """[{"digest":"5f2b0e5c"}]""");

        await Assert.ThrowsAnyAsync<RuntimeBinderException>(
            () => cluster.Service().GetSnapshots(info));
    }

    #endregion

    #region CreateSnapshot

    // The whole of a QEMU create: a POST to the same route the list is read from, differing only in verb,
    // carrying the name, the description and the RAM flag as the 1 Proxmox expects rather than a JSON true.
    [Fact]
    public async Task CreateSnapshot_OnAVirtualMachine_PostsTheNameDescriptionAndRamFlag()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var path = SnapshotPath(info);
        cluster.Accepts($"POST {path}");

        await cluster.Service().CreateSnapshot(info, Snap, "before patching", includeRam: true);

        Assert.Equal(
            """{"snapname":"snap1","description":"before patching","vmstate":1}""",
            cluster.Request(HttpMethod.Post, path).Body);
        Assert.Equal([path], cluster.Http.Paths);
    }

    // Not asking for RAM is a different body, not a different route. Pinned as an exact body because
    // vmstate is the one parameter of the four whose absence Proxmox reads as a meaningful default.
    [Fact]
    public async Task CreateSnapshot_OnAVirtualMachineWithoutRam_SendsVmStateAsZero()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var path = SnapshotPath(info);
        cluster.Accepts($"POST {path}");

        await cluster.Service().CreateSnapshot(info, Snap, "before patching", includeRam: false);

        Assert.Equal(
            """{"snapname":"snap1","description":"before patching","vmstate":0}""",
            cluster.Request(HttpMethod.Post, path).Body);
    }

    /// <remarks>
    /// INTENTIONAL, AND INVISIBLE FROM THE INTERFACE: a container has no RAM to capture, so the LXC
    /// branch does not pass <c>includeRam</c> on at all and the body carries no <c>vmstate</c> whichever
    /// way the caller asked. The endpoint takes the flag for both machine types and reports success
    /// either way, so a caller who asks a container for a RAM snapshot is told it worked and gets one
    /// without - which is the only reason to pin the true row as well as the false one.
    /// <para>
    /// It cannot be otherwise, and that is worth recording: the container client's
    /// <c>Snapshot(snapname, description)</c> has no <c>vmstate</c> parameter at all, so forwarding the
    /// flag does not compile. The discard is enforced by the Proxmox API surface, not just by this
    /// branch, and the mutation that would break it is losing the branch rather than losing the flag.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateSnapshot_OnAContainer_DiscardsTheRamFlagEntirely(bool includeRam)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);
        var path = SnapshotPath(info);
        cluster.Accepts($"POST {path}");

        await cluster.Service().CreateSnapshot(info, Snap, "before patching", includeRam);

        Assert.Equal(
            """{"snapname":"snap1","description":"before patching"}""",
            cluster.Request(HttpMethod.Post, path).Body);
        Assert.Equal([$"api2/json/nodes/pve1/lxc/{Vmid}/snapshot"], cluster.Http.Paths);
    }

    // The string reaches the API response verbatim, for both machine types.
    [Theory]
    [InlineData(ProxmoxVmType.QEMU)]
    [InlineData(ProxmoxVmType.LXC)]
    public async Task CreateSnapshot_ReportsTheSnapshotItCreatedAndTheMachineItCreatedItOn(
        ProxmoxVmType type)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, type);
        cluster.Accepts($"POST {SnapshotPath(info)}");

        Assert.Equal(
            $"snapshot {Snap} created on vmid {Vmid}",
            await cluster.Service().CreateSnapshot(info, Snap, "before patching", includeRam: false));
    }

    // Taking a snapshot leaves the machine running exactly as it was, so there is nothing for the state
    // poller to notice and it is not nudged. The counterpart assertion is in the region below.
    [Fact]
    public async Task CreateSnapshot_DoesNotAskTheStatePollerToRunAgain()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Accepts($"POST {SnapshotPath(info)}");

        await cluster.Service().CreateSnapshot(info, Snap, "before patching", includeRam: true);

        cluster.State.DidNotReceive().CheckState();
    }

    #endregion

    #region RevertSnapshot

    // A revert is a POST to a rollback route hung off the snapshot's own name, with an empty body: the
    // snapshot is named in the path, not in the payload.
    [Theory]
    [InlineData(ProxmoxVmType.QEMU, "qemu")]
    [InlineData(ProxmoxVmType.LXC, "lxc")]
    public async Task RevertSnapshot_PostsRollbackOnTheSnapshotsOwnRoute(
        ProxmoxVmType type, string segment)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, type);
        cluster.Accepts($"POST {SnapshotPath(info)}/{Snap}/rollback");

        await cluster.Service().RevertSnapshot(info, Snap);

        Assert.Equal(
            [$"api2/json/nodes/pve1/{segment}/{Vmid}/snapshot/{Snap}/rollback"], cluster.Http.Paths);
    }

    [Theory]
    [InlineData(ProxmoxVmType.QEMU)]
    [InlineData(ProxmoxVmType.LXC)]
    public async Task RevertSnapshot_ReportsTheSnapshotItRestoredAndTheMachineItRestoredItTo(
        ProxmoxVmType type)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, type);
        cluster.Accepts($"POST {SnapshotPath(info)}/{Snap}/rollback");

        Assert.Equal(
            $"snapshot {Snap} restored on vmid {Vmid}",
            await cluster.Service().RevertSnapshot(info, Snap));
    }

    // A revert restores whatever power state the snapshot was taken in, so it is the one snapshot
    // operation that can change a machine's state and the one that nudges the poller.
    [Fact]
    public async Task RevertSnapshot_AsksTheStatePollerToRunAgain()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Accepts($"POST {SnapshotPath(info)}/{Snap}/rollback");

        await cluster.Service().RevertSnapshot(info, Snap);

        cluster.State.Received(1).CheckState();
    }

    // The asymmetry stated in one place, because it is the most easily broken thing in this region: three
    // operations against the same machine and exactly one nudge, from the one that can change the power
    // state. The count is checked after each call rather than once at the end - a total of one is also
    // what a CheckState() moved from the revert to a sibling would produce, so counting only at the end
    // would pass the mutation this test exists to catch.
    [Fact]
    public async Task SnapshotOperations_NudgeTheStatePollerOnRevertOnly()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        Arrange(cluster, info);
        var service = cluster.Service();

        await service.CreateSnapshot(info, Snap, "before patching", includeRam: true);
        cluster.State.DidNotReceive().CheckState();

        await service.DeleteSnapshot(info, Snap);
        cluster.State.DidNotReceive().CheckState();

        await service.RevertSnapshot(info, Snap);
        cluster.State.Received(1).CheckState();
    }

    #endregion

    #region DeleteSnapshot

    // DELETE on the snapshot's own route, and note what is not there: no /delsnapshot segment, despite
    // the client method being named Delsnapshot(), and no body or query at all. The name of the method
    // and the shape of the request agree about nothing but the machine.
    [Theory]
    [InlineData(ProxmoxVmType.QEMU, "qemu")]
    [InlineData(ProxmoxVmType.LXC, "lxc")]
    public async Task DeleteSnapshot_SendsDeleteToTheSnapshotsOwnRouteWithNoExtraSegment(
        ProxmoxVmType type, string segment)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, type);
        var path = $"{SnapshotPath(info)}/{Snap}";
        cluster.Accepts($"DELETE {path}");

        await cluster.Service().DeleteSnapshot(info, Snap);

        Assert.Equal([$"api2/json/nodes/pve1/{segment}/{Vmid}/snapshot/{Snap}"], cluster.Http.Paths);
        Assert.Equal(string.Empty, cluster.Request(HttpMethod.Delete, path).Query);
    }

    [Theory]
    [InlineData(ProxmoxVmType.QEMU)]
    [InlineData(ProxmoxVmType.LXC)]
    public async Task DeleteSnapshot_ReportsTheSnapshotItDeletedAndTheMachineItDeletedItFrom(
        ProxmoxVmType type)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, type);
        cluster.Accepts($"DELETE {SnapshotPath(info)}/{Snap}");

        Assert.Equal(
            $"snapshot {Snap} deleted on vmid {Vmid}",
            await cluster.Service().DeleteSnapshot(info, Snap));
    }

    // Deleting a snapshot cannot change the power state, so nothing is nudged.
    [Fact]
    public async Task DeleteSnapshot_DoesNotAskTheStatePollerToRunAgain()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Accepts($"DELETE {SnapshotPath(info)}/{Snap}");

        await cluster.Service().DeleteSnapshot(info, Snap);

        cluster.State.DidNotReceive().CheckState();
    }

    #endregion

    #region The node the caller holds

    // None of the four consults ResolveNode, so all four are addressed to the node the caller's
    // ProxmoxVmInfo names - here the node the machine has already migrated off. GetConsole, ChangeNetwork
    // and MountIso all re-resolve first, so this asymmetry is a property of the snapshot region rather
    // than of the driver, and a stale node here surfaces as a Proxmox 500 rather than being corrected.
    [Theory]
    [InlineData("GetSnapshots")]
    [InlineData("CreateSnapshot")]
    [InlineData("RevertSnapshot")]
    [InlineData("DeleteSnapshot")]
    public async Task SnapshotOperation_IsAddressedToTheStaleNodeTheCallerHoldsRatherThanResolvingIt(
        string operation)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, node: "pve1");
        cluster.Migrates(Vmid, "pve7");
        Arrange(cluster, info);

        await Run(cluster.Service(), info, operation);

        // Every path begins pve1, and /cluster/resources - the read ResolveNode would have made, and the
        // only thing that could have discovered pve7 - was never asked for.
        Assert.All(cluster.Http.Paths, x => Assert.StartsWith("api2/json/nodes/pve1/", x));
    }

    #endregion

    /// <summary>All four snapshot routes on the node the caller holds, so one arrangement serves a theory.</summary>
    private static void Arrange(FakeProxmoxCluster cluster, ProxmoxVmInfo info)
    {
        var path = SnapshotPath(info);

        cluster.Answers($"GET {path}", "[]");
        cluster.Accepts($"POST {path}");
        cluster.Accepts($"POST {path}/{Snap}/rollback");
        cluster.Accepts($"DELETE {path}/{Snap}");
    }

    private static async Task Run(IProxmoxService service, ProxmoxVmInfo info, string operation)
    {
        switch (operation)
        {
            case "GetSnapshots":
                await service.GetSnapshots(info);
                break;
            case "CreateSnapshot":
                await service.CreateSnapshot(info, Snap, "before patching", includeRam: true);
                break;
            case "RevertSnapshot":
                await service.RevertSnapshot(info, Snap);
                break;
            case "DeleteSnapshot":
                await service.DeleteSnapshot(info, Snap);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static string SnapshotPath(ProxmoxVmInfo info) =>
        FakeProxmoxCluster.VmPath(info, "/snapshot");

    /// <summary>One snapshot entry with every field but <paramref name="omitted"/>, which is left out.</summary>
    private static string Fields(string omitted) =>
        string.Join(',', new[]
        {
            ("name", "\"snap1\""),
            ("description", "\"before patching\""),
            ("parent", "\"snap0\""),
            ("vmstate", "1"),
            ("snaptime", "1700000000"),
        }
        .Where(x => x.Item1 != omitted)
        .Select(x => $"\"{x.Item1}\":{x.Item2}"));
}
