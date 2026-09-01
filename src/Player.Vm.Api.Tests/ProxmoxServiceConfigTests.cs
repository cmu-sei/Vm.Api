// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// Reading a Vm's live Proxmox config and writing one adapter of it back: the two halves of the network
/// control in the Vm UI. Driven through a substituted transport, so what these pin is the request
/// Proxmox would have received - see <see cref="FakeProxmoxCluster"/> for why the seam is the socket.
/// </summary>
/// <remarks>
/// <para>
/// Almost none of this is visible from the source. The number of config reads is a performance contract
/// stated only in a comment on the interface; the update verb differs between a Vm and a container
/// because the generated client offers <c>UpdateVmAsync</c> for one and only <c>UpdateVm</c> for the
/// other; and the string that lands in <c>net0</c> is assembled by a private helper that no test can
/// reach except through here. All three are things a caller depends on and a refactor can silently
/// change.
/// </para>
/// <para>
/// The bridge validation is the other reason this class exists. Writing an adapter onto a bridge that
/// does not exist on the node leaves the Vm unreachable with no error the user can act on, so the
/// listing is read first and the write is not attempted at all when the target is absent - which is an
/// assertion about a request that did *not* go out.
/// </para>
/// </remarks>
public class ProxmoxServiceConfigTests
{
    private const int Vmid = 100;

    private const string Mac = "AA:BB:CC:DD:EE:FF";

    /// <summary>A Vm with one adapter, an optical drive with a medium in it, and a system disk.</summary>
    private const string QemuConfig = $$"""
        {"net0":"virtio={{Mac}},bridge=vmbr1,firewall=1",
         "ide2":"nfs:iso/thing.iso,media=cdrom,size=700M",
         "scsi0":"local-lvm:vm-100-disk-0,size=32G"}
        """;

    /// <summary>A container's adapter, which is spelled differently: named, and with hwaddr not virtio.</summary>
    private const string LxcConfig = $$"""
        {"net0":"name=eth0,bridge=vmbr1,hwaddr={{Mac}},ip=dhcp"}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region GetVmConfigSummary

    // One read, and everything the API response needs taken out of it. The request count is the
    // assertion that matters as much as the values: the interface documents the single read as
    // deliberate, because a second read per Vm is paid on every Vm in every list response.
    [Fact]
    public async Task GetVmConfigSummary_ReadsTheQemuConfigOnceAndReportsItsNetworksAndItsDrive()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var config = FakeProxmoxCluster.VmPath(info, "/config");
        cluster.Answers($"GET {config}", QemuConfig);

        var summary = await cluster.Service().GetVmConfigSummary(info, Ct);

        Assert.Equal(new Dictionary<string, string> { ["net0"] = "vmbr1" }, summary.CurrentNetworks);
        Assert.True(summary.HasCdromDrive);
        Assert.Equal([FakeProxmoxCluster.ClusterResources, config], cluster.Http.Paths);
        Assert.Equal("?current=1", cluster.Request(HttpMethod.Get, config).Query);
    }

    // Every adapter the Vm has, keyed by the adapter the caller then changes - so a multi-homed Vm shows
    // one entry per NIC rather than whichever one happened to be parsed first.
    [Fact]
    public async Task GetVmConfigSummary_ReportsEveryAdapterKeyedByItsAdapterId()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", $$"""
            {"net0":"virtio={{Mac}},bridge=vmbr1","net1":"e1000=11:22:33:44:55:66,bridge=vmbr2",
             "net3":"virtio=77:88:99:AA:BB:CC,bridge=vmbr1"}
            """);

        var summary = await cluster.Service().GetVmConfigSummary(info, Ct);

        Assert.Equal(
            new Dictionary<string, string> { ["net0"] = "vmbr1", ["net1"] = "vmbr2", ["net3"] = "vmbr1" },
            summary.CurrentNetworks);
    }

    // Whether a mount is offered at all. This comes out of the same GetCdromDrives helper MountIso
    // selects its target from, so what this reports and what a mount can find cannot disagree - a Vm
    // told it has a drive is a Vm on which the mount will find one.
    [Theory]
    [InlineData("""{"ide2":"nfs:iso/thing.iso,media=cdrom,size=700M"}""", true)]
    [InlineData("""{"ide2":"none,media=cdrom"}""", true)]
    [InlineData("""{"sata0":"local:iso/thing.iso,media=cdrom"}""", true)]
    [InlineData("""{"scsi0":"local-lvm:vm-100-disk-0,size=32G"}""", false)]
    [InlineData("{}", false)]
    public async Task GetVmConfigSummary_ReportsADriveOnlyWhenTheConfigHasOneWithMediaCdrom(
        string config, bool hasDrive)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", config);

        var summary = await cluster.Service().GetVmConfigSummary(info, Ct);

        Assert.Equal(hasDrive, summary.HasCdromDrive);
    }

    // A container is addressed under /lxc, and its adapter definition has none of the shape a Vm's has -
    // no model=mac token at all - so the bridge is the one field both spellings share and the only one
    // this summary needs.
    [Fact]
    public async Task GetVmConfigSummary_OnAContainer_ReadsTheLxcConfigAndStillFindsTheBridge()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);
        var config = $"api2/json/nodes/pve1/lxc/{Vmid}/config";
        cluster.Answers($"GET {config}", LxcConfig);

        var summary = await cluster.Service().GetVmConfigSummary(info, Ct);

        Assert.Equal(new Dictionary<string, string> { ["net0"] = "vmbr1" }, summary.CurrentNetworks);
        Assert.Equal([FakeProxmoxCluster.ClusterResources, config], cluster.Http.Paths);
    }

    // False for a container by construction rather than by looking, which is what makes it safe: an LXC
    // container has no optical drive, so a config that appeared to describe one would be describing
    // something a mount could not use.
    [Fact]
    public async Task GetVmConfigSummary_OnAContainer_ReportsNoDriveWithoutConsultingTheConfigsDrives()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);
        cluster.Answers(
            $"GET api2/json/nodes/pve1/lxc/{Vmid}/config",
            """{"net0":"name=eth0,bridge=vmbr1","ide2":"nfs:iso/thing.iso,media=cdrom"}""");

        Assert.False((await cluster.Service().GetVmConfigSummary(info, Ct)).HasCdromDrive);
    }

    // A NIC defined with no network attached is not a current network. Reporting it would put a blank
    // entry in the UI's dropdown and, worse, offer it as an adapter to change - which ChangeNetwork then
    // refuses.
    /// <remarks>
    /// The filter drops an adapter with a blank id as well, and that half of it is not reachable from
    /// here: the client takes the adapter id from the config key, and every key beginning with "net" is
    /// taken as an adapter, so a key of "net" or "netx" arrives with that string as its id rather than
    /// with a blank one. Only the bridge half of the filter can be shown through the API.
    /// </remarks>
    [Fact]
    public async Task GetVmConfigSummary_DropsAnAdapterWithNoBridge()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", $$"""
            {"net0":"virtio={{Mac}}","net1":"virtio=11:22:33:44:55:66,bridge=vmbr2"}
            """);

        var summary = await cluster.Service().GetVmConfigSummary(info, Ct);

        Assert.Equal(new Dictionary<string, string> { ["net1"] = "vmbr2" }, summary.CurrentNetworks);
    }

    // A Vm with no networking at all is normal, not an error: an empty map, which the NIC options
    // builder and the response both handle, rather than a null the caller has to guard.
    [Fact]
    public async Task GetVmConfigSummary_WhenTheVmHasNoNetworks_ReportsAnEmptyMapRatherThanNull()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", "{}");

        var summary = await cluster.Service().GetVmConfigSummary(info, Ct);

        Assert.NotNull(summary.CurrentNetworks);
        Assert.Empty(summary.CurrentNetworks);
    }

    // A vmid Proxmox has never heard of fails before any node is addressed, because there is no node to
    // address: the stored one is whatever it was when the Vm last existed.
    [Fact]
    public async Task GetVmConfigSummary_WhenProxmoxDoesNotKnowTheVmid_ThrowsWithoutReadingAConfig()
    {
        var cluster = new FakeProxmoxCluster();
        var info = new ProxmoxVmInfo { Id = Vmid, Node = "pve1", Type = ProxmoxVmType.QEMU };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.Service().GetVmConfigSummary(info, Ct));

        Assert.Equal($"Could not find vmid {Vmid} in Proxmox", ex.Message);
        Assert.Equal([FakeProxmoxCluster.ClusterResources], cluster.Http.Paths);
    }

    // The stored node goes stale the moment a Vm migrates, and only the state poller refreshes it. The
    // config read is addressed to the node the cluster currently reports, not the one the caller holds,
    // so a read between a migration and the next poll still lands.
    [Fact]
    public async Task GetVmConfigSummary_AfterAMigration_ReadsTheConfigFromTheNodeProxmoxNowReports()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Migrates(Vmid, "pve2");
        cluster.Answers($"GET api2/json/nodes/pve2/qemu/{Vmid}/config", QemuConfig);

        await cluster.Service().GetVmConfigSummary(info, Ct);

        Assert.Equal(
            [FakeProxmoxCluster.ClusterResources, $"api2/json/nodes/pve2/qemu/{Vmid}/config"],
            cluster.Http.Paths);
        Assert.Equal("pve2", info.Node);
    }

    #endregion

    #region ChangeNetwork

    // The whole gesture, in order: find the Vm, read what its adapter currently is, check the target
    // bridge exists on that node, then write back that one adapter and nothing else. Four requests, and
    // a body carrying a single key - a config update that named any other key would reset it, because
    // PVE treats an omitted parameter as unchanged but a supplied one as authoritative.
    [Fact]
    public async Task ChangeNetwork_ResolvesReadsValidatesThenWritesOnlyTheOneAdapter()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var config = FakeProxmoxCluster.VmPath(info, "/config");
        var network = FakeProxmoxCluster.NodePath("pve1", "/network");
        cluster.Answers($"GET {config}", QemuConfig);
        cluster.Answers($"GET {network}", Bridges("vmbr1", "vmbr9"));
        cluster.Accepts($"POST {config}");

        await cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct);

        Assert.Equal(
            [FakeProxmoxCluster.ClusterResources, config, network, config],
            cluster.Http.Paths);
        Assert.Equal(
            $$"""{"net0":"virtio={{Mac}},bridge=vmbr9,firewall=1"}""",
            cluster.Request(HttpMethod.Post, config).Body);
    }

    // The asymmetry: a Vm's config update is a POST and a container's is a PUT. Nothing in
    // ChangeNetwork's own source says so - the generated client offers Qemu.Config.UpdateVmAsync, which
    // is the POST "update asynchronously" route, and offers a container only Config.UpdateVm. Only the
    // POST is stubbed here and only the PUT there, so either one going out by the other verb is a
    // request nothing answers.
    [Fact]
    public async Task ChangeNetwork_OnAContainer_WritesTheConfigWithPutWhereAVmUsesPost()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);
        var config = $"api2/json/nodes/pve1/lxc/{Vmid}/config";
        cluster.Answers($"GET {config}", LxcConfig);
        cluster.Answers($"GET {FakeProxmoxCluster.NodePath("pve1", "/network")}", Bridges("vmbr1", "vmbr9"));
        cluster.Accepts($"PUT {config}");

        await cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct);

        Assert.Equal(
            $$"""{"net0":"name=eth0,bridge=vmbr9,hwaddr={{Mac}},ip=dhcp"}""",
            cluster.Request(HttpMethod.Put, config).Body);
    }

    // The definition is rewritten, not rebuilt: the bridge token is replaced where it stands and every
    // other token survives in place. A rebuild would drop the MAC address - which is a Vm's identity on
    // the network and what a DHCP reservation is keyed on - along with the firewall, VLAN tag and rate
    // limit a view has configured.
    /// <remarks>
    /// Every arrangement here has a bridge token to replace, and no arrangement can avoid having one:
    /// the helper appends "bridge=" when the definition has none, but ChangeNetwork refuses an adapter
    /// whose bridge is blank before it gets there, and an adapter's bridge is blank exactly when its
    /// definition has no bridge token. The appending branch is therefore unreachable through the only
    /// caller - dead until something calls the helper with a definition its own caller would reject.
    /// </remarks>
    [Theory]
    [InlineData("virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr1", "virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr9")]
    [InlineData(
        "virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr1,firewall=1,tag=100,rate=10,link_down=1",
        "virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr9,firewall=1,tag=100,rate=10,link_down=1")]
    [InlineData("bridge=vmbr1,virtio=AA:BB:CC:DD:EE:FF", "bridge=vmbr9,virtio=AA:BB:CC:DD:EE:FF")]
    [InlineData("e1000=AA:BB:CC:DD:EE:FF,bridge=vmbr1,mtu=9000", "e1000=AA:BB:CC:DD:EE:FF,bridge=vmbr9,mtu=9000")]
    public async Task ChangeNetwork_ReplacesTheBridgeTokenInPlaceAndKeepsEveryOtherToken(
        string current, string written)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var config = FakeProxmoxCluster.VmPath(info, "/config");
        cluster.Answers($"GET {config}", $$"""{"net0":"{{current}}"}""");
        cluster.Answers($"GET {FakeProxmoxCluster.NodePath("pve1", "/network")}", Bridges("vmbr9"));
        cluster.Accepts($"POST {config}");

        await cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct);

        Assert.Equal($$"""{"net0":"{{written}}"}""", cluster.Request(HttpMethod.Post, config).Body);
    }

    // The key written is parsed off the adapter's own id rather than assumed to be net0, and it is the
    // only key in the body - so changing one adapter on a multi-homed Vm leaves the others alone.
    [Fact]
    public async Task ChangeNetwork_WritesTheAdapterItWasAskedFor_LeavingTheOtherAdaptersOutOfTheBody()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var config = FakeProxmoxCluster.VmPath(info, "/config");
        cluster.Answers($"GET {config}", $$"""
            {"net0":"virtio={{Mac}},bridge=vmbr1","net3":"e1000=11:22:33:44:55:66,bridge=vmbr2"}
            """);
        cluster.Answers($"GET {FakeProxmoxCluster.NodePath("pve1", "/network")}", Bridges("vmbr9"));
        cluster.Accepts($"POST {config}");

        await cluster.Service().ChangeNetwork(info, "net3", "vmbr9", Ct);

        Assert.Equal(
            """{"net3":"e1000=11:22:33:44:55:66,bridge=vmbr9"}""",
            cluster.Request(HttpMethod.Post, config).Body);
    }

    // Three ways an adapter cannot be changed, one message: it is not in the config at all, it is there
    // with no network attached, or its definition is empty. The middle one is the case a stale UI
    // produces - an adapter listed from an earlier read that has since been detached.
    /// <remarks>
    /// The third arrangement does not isolate the blank-definition guard, and nothing can: an adapter
    /// with no definition has no bridge either, so the bridge guard has already refused it. The guard is
    /// belt and braces against a client that parsed a definition it could not reproduce.
    /// </remarks>
    [Theory]
    [InlineData("""{"net1":"virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr1"}""")]
    [InlineData("""{"net0":"virtio=AA:BB:CC:DD:EE:FF"}""")]
    [InlineData("""{"net0":""}""")]
    public async Task ChangeNetwork_WhenTheAdapterCannotBeChanged_ThrowsWithoutValidatingOrWriting(
        string config)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct));

        Assert.Equal($"Could not find network adapter net0 on vmid {Vmid}", ex.Message);
        Assert.Equal(
            [FakeProxmoxCluster.ClusterResources, FakeProxmoxCluster.VmPath(info, "/config")],
            cluster.Http.Paths);
        cluster.State.DidNotReceive().CheckState();
    }

    // Refused before anything is written, which is the entire point of reading the listing first: a
    // config update naming a bridge the node does not have is accepted by PVE and leaves the Vm on a
    // nonexistent network, unreachable, with nothing in the API response to say why.
    [Fact]
    public async Task ChangeNetwork_WhenTheTargetBridgeIsNotOnTheNode_IsRefusedBeforeAnythingIsWritten()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var config = FakeProxmoxCluster.VmPath(info, "/config");
        var network = FakeProxmoxCluster.NodePath("pve1", "/network");
        cluster.Answers($"GET {config}", QemuConfig);
        cluster.Answers($"GET {network}", Bridges("vmbr1", "vmbr2"));

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct));

        Assert.Equal(
            "The target network 'vmbr9' does not exist on Proxmox node 'pve1'.",
            ex.Message);
        Assert.Equal([FakeProxmoxCluster.ClusterResources, config, network], cluster.Http.Paths);
        Assert.Equal("?type=any_bridge", cluster.Request(HttpMethod.Get, network).Query);
    }

    // The listing is asked for by type rather than filtered client-side, so what is validated against is
    // what PVE itself considers assignable - a bond or a physical port is not offered as a target even
    // though it appears in an unfiltered listing.
    [Fact]
    public async Task ChangeNetwork_ValidatesAgainstTheNodesBridgesRatherThanEveryInterfaceOnIt()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var config = FakeProxmoxCluster.VmPath(info, "/config");
        var network = FakeProxmoxCluster.NodePath("pve1", "/network");
        cluster.Answers($"GET {config}", QemuConfig);
        cluster.Answers($"GET {network}", Bridges("vmbr1", "vmbr9"));
        cluster.Accepts($"POST {config}");

        await cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct);

        Assert.Equal("?type=any_bridge", cluster.Request(HttpMethod.Get, network).Query);
    }

    // A node that cannot be asked what bridges it has is a server fault, not a bad request, and it stops
    // the change: an unreadable listing is indistinguishable from an empty one, and treating it as empty
    // would refuse every valid target while treating it as permission would write blind. The refusal here
    // carries a Proxmox error object, because what the node said is the only actionable part of the
    // message - a bare status leaves it empty, as ProxmoxServiceConsoleTests characterizes.
    [Fact]
    public async Task ChangeNetwork_WhenTheBridgeListingFails_ThrowsAndWritesNothing()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var config = FakeProxmoxCluster.VmPath(info, "/config");
        var network = FakeProxmoxCluster.NodePath("pve1", "/network");
        cluster.Answers($"GET {config}", QemuConfig);
        cluster.Rejects($"GET {network}", "no such node 'pve1'");

        var ex = await Assert.ThrowsAsync<Exception>(
            () => cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct));

        Assert.StartsWith("Could not list Proxmox networks on node pve1: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no such node 'pve1'", ex.Message, StringComparison.Ordinal);
        Assert.Equal([FakeProxmoxCluster.ClusterResources, config, network], cluster.Http.Paths);
    }

    // The state poller is nudged once the change has gone through, so the UI shows the new network
    // without waiting out the poll interval - and is not nudged when nothing changed, because a poll
    // that finds the same state costs a cluster-wide read for nothing.
    [Fact]
    public async Task ChangeNetwork_AsksTheStatePollerToRunAgainOnceTheChangeIsThrough()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var config = FakeProxmoxCluster.VmPath(info, "/config");
        cluster.Answers($"GET {config}", QemuConfig);
        cluster.Answers($"GET {FakeProxmoxCluster.NodePath("pve1", "/network")}", Bridges("vmbr9"));
        cluster.Accepts($"POST {config}");

        await cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct);

        cluster.State.Received(1).CheckState();
    }

    [Fact]
    public async Task ChangeNetwork_WhenTheChangeIsRefused_DoesNotAskTheStatePollerToRunAgain()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", QemuConfig);
        cluster.Answers($"GET {FakeProxmoxCluster.NodePath("pve1", "/network")}", Bridges("vmbr1"));

        await Assert.ThrowsAsync<BadRequestException>(
            () => cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct));

        cluster.State.DidNotReceive().CheckState();
    }

    // All three requests follow the Vm, not the caller's stale copy of where it was. The bridge listing
    // matters most here: bridges are per-node, so validating against the node the Vm has left could
    // approve a target the node it is now on does not have.
    [Fact]
    public async Task ChangeNetwork_AfterAMigration_ReadsValidatesAndWritesOnTheNodeProxmoxNowReports()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Migrates(Vmid, "pve2");
        var config = $"api2/json/nodes/pve2/qemu/{Vmid}/config";
        var network = FakeProxmoxCluster.NodePath("pve2", "/network");
        cluster.Answers($"GET {config}", QemuConfig);
        cluster.Answers($"GET {network}", Bridges("vmbr9"));
        cluster.Accepts($"POST {config}");

        await cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct);

        Assert.Equal(
            [FakeProxmoxCluster.ClusterResources, config, network, config],
            cluster.Http.Paths);
    }

    // A vmid Proxmox has never heard of, on the write path as on the read path: nothing is attempted
    // against a node, because the stored node is only as current as the Vm's last existence.
    [Fact]
    public async Task ChangeNetwork_WhenProxmoxDoesNotKnowTheVmid_ThrowsWithoutReadingAConfig()
    {
        var cluster = new FakeProxmoxCluster();
        var info = new ProxmoxVmInfo { Id = Vmid, Node = "pve1", Type = ProxmoxVmType.QEMU };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.Service().ChangeNetwork(info, "net0", "vmbr9", Ct));

        Assert.Equal($"Could not find vmid {Vmid} in Proxmox", ex.Message);
        Assert.Equal([FakeProxmoxCluster.ClusterResources], cluster.Http.Paths);
    }

    #endregion

    /// <summary>The node's bridge listing, as <c>/nodes/{node}/network?type=any_bridge</c> answers it.</summary>
    private static string Bridges(params string[] bridges) =>
        "[" + string.Join(',', bridges.Select(x => $$"""{"iface":"{{x}}","type":"bridge","active":1}""")) + "]";
}
