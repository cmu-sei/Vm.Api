// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// What a console request turns into on the wire, and what the browser is handed back. The URL built
/// here is the whole of the console feature: it is passed to a noVNC client verbatim, so a wrong host, a
/// wrong guest type segment or a ticket that was not escaped is a console that never opens, with nothing
/// in this application's logs to say why.
/// </summary>
/// <remarks>
/// <para>
/// Driven through a substituted transport rather than a substituted <c>PveClient</c> - see
/// <see cref="FakeProxmoxCluster"/> - because the vncproxy route and the <c>vncwebsocket</c> route inside
/// the returned URL have to agree with each other about the node and the guest type, and only a test
/// that can see the request as Proxmox received it can say that they do.
/// </para>
/// <para>
/// This class also carries the coverage for the private <c>ResolveNode</c>, of which
/// <c>GetConsole</c> is the simplest caller: the same node refresh sits in front of
/// <c>GetVmConfigSummary</c>, <c>ChangeNetwork</c>, <c>MountIso</c>, <c>GetCurrentNodeForVm</c> and the
/// stale-node retry in <c>BulkPowerOperation</c>.
/// </para>
/// <para>
/// Every service here is built with a null <c>VmContext</c>, which is an assertion in itself: a console
/// is produced from the cluster alone, so a database read appearing on this path would fail these tests
/// rather than quietly cost a query per console open.
/// </para>
/// </remarks>
public class ProxmoxServiceConsoleTests
{
    private const int Vmid = 100;

    /// <summary>
    /// Shaped like a real PVE VNC ticket, which is a base64 blob: it carries the three characters -
    /// <c>+</c>, <c>/</c> and <c>=</c> - that change meaning in a query string, plus the <c>:</c> of the
    /// prefix. A ticket without them cannot tell an escaped URL from an unescaped one.
    /// </summary>
    private const string Ticket = "PVEVNC:AA+bb/cc==";

    private const string EncodedTicket = "PVEVNC%3AAA%2Bbb%2Fcc%3D%3D";

    private const string VncproxyBody = "{\"websocket\":1}";

    #region A VM that has no console to give

    /// <summary>
    /// The vmid is matched against what the cluster reports rather than trusted, and a vmid the cluster has
    /// never heard of is an error rather than a power state - a client told <c>PowerState.Unknown</c> would
    /// sit retrying a console that can never exist. The bystander machine is registered so that this is a
    /// test about the vmid not matching, not about an empty cluster.
    /// </summary>
    /// <remarks>
    /// This is reached through a catch rather than a null check: <c>PveClient.GetVmAsync</c> throws
    /// <see cref="ArgumentException"/> for a vmid the cluster does not have, so <c>ResolveNode</c> catches
    /// it and returns null to give its callers one "not found" shape to test. Should the client ever start
    /// returning null instead, that catch becomes dead code and this test still passes - the behaviour is
    /// pinned, the mechanism is not.
    /// </remarks>
    [Fact]
    public async Task GetConsole_WhenProxmoxDoesNotKnowTheVmid_ThrowsAndNeverAsksForATicket()
    {
        var cluster = new FakeProxmoxCluster();
        cluster.Has(101);
        var info = new ProxmoxVmInfo
        {
            Id = Vmid,
            Node = FakeProxmoxCluster.DefaultNode,
            Type = ProxmoxVmType.QEMU,
        };

        var exception = await Assert.ThrowsAsync<Exception>(() => cluster.Service().GetConsole(info));

        Assert.Equal($"Could not find vmid {Vmid} in Proxmox", exception.Message);

        // No vncproxy request: the cluster read is the only thing that happened, so there is no ticket
        // issued for a machine nobody can identify.
        Assert.Equal([FakeProxmoxCluster.ClusterResources], cluster.Http.Paths);
    }

    // Nothing is reported to the state poller for a vmid the cluster cannot resolve, so a machine that
    // has been destroyed in Proxmox is not written back into this application's state as if it were
    // still there.
    [Fact]
    public async Task GetConsole_WhenProxmoxDoesNotKnowTheVmid_TellsTheStatePollerNothing()
    {
        var cluster = new FakeProxmoxCluster();
        var info = new ProxmoxVmInfo { Id = Vmid, Node = FakeProxmoxCluster.DefaultNode };

        await Assert.ThrowsAsync<Exception>(() => cluster.Service().GetConsole(info));

        await cluster.State.DidNotReceive().UpdateVm(Arg.Any<IClusterResourceVm>());
    }

    // The load-bearing behaviour of this method, and the one that is invisible from the outside. Proxmox
    // will hand out a vncproxy ticket for a machine that is not running, and the websocket it opens never
    // completes an RFB handshake - so a client given that ticket waits forever with no error. The power
    // state is therefore read first and the ticket request skipped entirely, which is why "no vncproxy
    // request at all" is asserted here rather than just "Url is null".
    [Theory]
    [InlineData("stopped", PowerState.Off)]
    [InlineData("paused", PowerState.Suspended)]
    [InlineData("unknown", PowerState.Unknown)]
    public async Task GetConsole_WhenTheVmIsNotRunning_ReportsThePowerStateAndAsksForNoTicket(
        string status, PowerState expected)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, status: status);

        var console = await cluster.Service().GetConsole(info);

        Assert.Equal(expected, console.PowerState);
        Assert.Null(console.Url);
        Assert.Null(console.Ticket);
        Assert.Equal([FakeProxmoxCluster.ClusterResources], cluster.Http.Paths);
    }

    #endregion

    #region The running console

    // The exact URL, because it is consumed by a client outside this repository and every part of it is
    // load-bearing: the scheme a noVNC client needs, the node and guest type the websocket route is
    // addressed to, and the port and ticket pairing that vncproxy just issued. The port arrives as a
    // string from one Proxmox version and a number from another, and is only interpolated, so both
    // produce the same URL.
    [Theory]
    [InlineData("\"5900\"")]
    [InlineData("5900")]
    public async Task GetConsole_WhenTheVmIsRunning_BuildsTheWebsocketUrlProxmoxIssuedTheTicketFor(
        string portJson)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, "/vncproxy")}", Vncproxy(portJson));

        var console = await cluster.Service().GetConsole(info);

        Assert.Equal(
            $"wss://{FakeProxmoxCluster.Host}/api2/json/nodes/pve1/qemu/{Vmid}" +
            $"/vncwebsocket?port=5900&vncticket={EncodedTicket}",
            console.Url);
        Assert.Equal(PowerState.On, console.PowerState);
    }

    // The ticket is escaped where it is a query parameter and raw where it is a value: a client that
    // opens the URL needs the escaped form, and one that reads Ticket to authenticate the websocket
    // itself needs the bytes Proxmox issued. Getting this wrong in either direction - dropping the
    // encode, or reporting the encoded ticket - is a console that fails only for the tickets that happen
    // to contain a + or a /, which is most of them.
    [Fact]
    public async Task GetConsole_EscapesTheTicketInTheUrlAndReportsItRawAlongside()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, "/vncproxy")}", Vncproxy());

        var console = await cluster.Service().GetConsole(info);

        Assert.Equal(Ticket, console.Ticket);
        Assert.Contains(EncodedTicket, console.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(Ticket, console.Url, StringComparison.Ordinal);
    }

    // What Proxmox is asked for: one POST to the machine's own vncproxy route, requesting a websocket
    // rather than a bare VNC port, and preceded only by the cluster read that resolved the node. The
    // websocket flag is what makes the ticket usable from a browser at all.
    [Fact]
    public async Task GetConsole_AsksTheVmsOwnVncproxyRouteForAWebsocketTicketAndNothingElse()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var path = FakeProxmoxCluster.VmPath(info, "/vncproxy");
        cluster.Answers($"POST {path}", Vncproxy());

        await cluster.Service().GetConsole(info);

        Assert.Equal([FakeProxmoxCluster.ClusterResources, path], cluster.Http.Paths);
        Assert.Equal(VncproxyBody, cluster.Request(HttpMethod.Post, path).Body);
        Assert.Equal(
            $"PVEAPIToken {FakeProxmoxCluster.ApiToken}",
            cluster.Request(HttpMethod.Post, path).Authorization);
    }

    // The reverse-proxied deployment, where the Proxmox hosts are not reachable from a player's browser.
    // The websocket is addressed to the proxy and the real host rides along in a query parameter for the
    // proxy to route on, so both halves have to be right or every console in that deployment breaks -
    // and the default deployment above would not notice.
    [Fact]
    public async Task GetConsole_WhenHostRewritingIsOn_TargetsTheProxyAndCarriesTheRealHostAsAQueryParam()
    {
        var cluster = new FakeProxmoxCluster
        {
            RewriteHost = new RewriteHostOptions
            {
                RewriteHost = true,
                RewriteHostUrl = "console.example.test",
                RewriteHostQueryParam = "vmhost",
            },
        };
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, "/vncproxy")}", Vncproxy());

        var console = await cluster.Service().GetConsole(info);

        Assert.Equal(
            $"wss://console.example.test/api2/json/nodes/pve1/qemu/{Vmid}" +
            $"/vncwebsocket?port=5900&vncticket={EncodedTicket}&vmhost={FakeProxmoxCluster.Host}",
            console.Url);
    }

    // A container's console lives under /lxc, and the segment has to match in two independent places:
    // the route the ticket is requested from, and the route inside the URL the client will open. Both are
    // built from ProxmoxVmInfo.Type, so a container asked for over the qemu route gets a 500 from
    // Proxmox and a container given a qemu websocket URL gets a ticket for a machine that is not it.
    [Fact]
    public async Task GetConsole_ForAContainer_AddressesLxcInBothTheTicketRequestAndTheReturnedUrl()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);
        var path = FakeProxmoxCluster.VmPath(info, "/vncproxy");
        cluster.Answers($"POST {path}", Vncproxy());

        var console = await cluster.Service().GetConsole(info);

        Assert.Equal($"api2/json/nodes/pve1/lxc/{Vmid}/vncproxy", path);
        Assert.Equal([FakeProxmoxCluster.ClusterResources, path], cluster.Http.Paths);
        Assert.Equal(
            $"wss://{FakeProxmoxCluster.Host}/api2/json/nodes/pve1/lxc/{Vmid}" +
            $"/vncwebsocket?port=5900&vncticket={EncodedTicket}",
            console.Url);
    }

    // A refused ticket request fails the console rather than returning one that cannot connect, and what
    // Proxmox said about it reaches the caller - the usual cause is a token whose privileges do not
    // include VM.Console, which is only diagnosable from the message.
    [Fact]
    public async Task GetConsole_WhenProxmoxRefusesTheTicket_ThrowsWhatProxmoxSaidAboutIt()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Rejects(
            $"POST {FakeProxmoxCluster.VmPath(info, "/vncproxy")}",
            "Permission check failed (/vms/100, VM.Console)");

        var exception = await Assert.ThrowsAsync<Exception>(() => cluster.Service().GetConsole(info));

        Assert.Contains(
            "Permission check failed (/vms/100, VM.Console)", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal that carries no <c>errors</c> object - which is what a misconfigured token's 401 and most
    /// gateway failures look like - still fails the console, and that is all it does.
    /// </summary>
    /// <remarks>
    /// Characterizing a real diagnosability weakness, not asserting a desirable behaviour.
    /// <c>Result.GetError()</c> is built only from an <c>errors</c> object in the response body, so a bare
    /// 401 or 502 produces <c>throw new Exception("")</c>: the status code, the reason phrase and the
    /// route are all discarded, and what the operator sees is an empty error with no indication that the
    /// token was rejected. Every other refusal path in this service wraps <c>GetError()</c> in a message
    /// naming the operation and the vmid, which is why this one is worth recording. Fixing it - passing
    /// the status through, as <c>WaitForTaskToFinish</c> does - would redden this test, and that is the
    /// point of it.
    /// </remarks>
    [Fact]
    public async Task GetConsole_WhenProxmoxRefusesTheTicketWithNoErrorDetail_ThrowsWithNoMessageAtAll()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Http.AnswersJson(
            $"POST {FakeProxmoxCluster.VmPath(info, "/vncproxy")}",
            FakeProxmoxCluster.Data("null"),
            HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<Exception>(() => cluster.Service().GetConsole(info));

        Assert.Equal(string.Empty, exception.Message);
    }

    #endregion

    #region Resolving the node the VM is on now

    // Every console open refreshes this application's view of the machine from the cluster read it had to
    // make anyway, which is how a power state changed outside Player is picked up without waiting out the
    // poll interval - the same read the console decision itself is made from, so the two cannot disagree.
    [Fact]
    public async Task GetConsole_ReportsTheLiveResourceItResolvedToTheStatePoller()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, "/vncproxy")}", Vncproxy());

        await cluster.Service().GetConsole(info);

        await cluster.State.Received(1).UpdateVm(Arg.Any<IClusterResourceVm>());
    }

    // The reason ResolveNode exists. ProxmoxVmInfo.Node is only refreshed by the state poller, so between
    // a migration and the next poll every caller holds a node the machine has left - and a vncproxy
    // request to that node fails. The stored node is corrected in place from the cluster read, so the
    // ticket is requested from the node the machine is on now and the correction outlives this call for
    // whatever the caller does next.
    [Fact]
    public async Task GetConsole_WhenTheVmHasMigrated_CorrectsTheStoredNodeAndAsksTheNodeItIsOnNow()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Migrates(Vmid, "pve2");
        var path = FakeProxmoxCluster.VmPath("pve2", ProxmoxVmType.QEMU, Vmid, "/vncproxy");
        cluster.Answers($"POST {path}", Vncproxy());

        var console = await cluster.Service().GetConsole(info);

        Assert.Equal("pve2", info.Node);

        // Nothing was addressed to pve1: the stale node has no rule at all, so a request to it would have
        // failed the test outright rather than being tolerated.
        Assert.Equal([FakeProxmoxCluster.ClusterResources, path], cluster.Http.Paths);
        Assert.Contains($"/nodes/pve2/qemu/{Vmid}/vncwebsocket", console.Url, StringComparison.Ordinal);
    }

    // The cluster is read once per console open and the vmid matched client-side, rather than asked about
    // one machine. Worth pinning because the console is opened per player per VM and this read returns
    // every resource in the cluster: a second read here would double that on every open.
    [Fact]
    public async Task GetConsole_ReadsTheClusterResourceListOnceAndFiltersToTheVmidItself()
    {
        var cluster = new FakeProxmoxCluster();
        cluster.Has(101, node: "pve2");
        var info = cluster.Has(Vmid);
        cluster.Has(102, ProxmoxVmType.LXC, node: "pve3");
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, "/vncproxy")}", Vncproxy());

        await cluster.Service().GetConsole(info);

        var request = Assert.Single(
            cluster.Requests(HttpMethod.Get, FakeProxmoxCluster.ClusterResources));

        Assert.Equal("?type=vm", request.Query);
    }

    #endregion

    /// <summary>
    /// The vncproxy response, which must carry both keys: the service reads them off an ExpandoObject,
    /// where a missing key throws a <c>RuntimeBinderException</c> rather than reading as null.
    /// </summary>
    private static string Vncproxy(string portJson = "\"5900\"") =>
        $"{{\"ticket\":\"{Ticket}\",\"port\":{portJson}}}";
}
