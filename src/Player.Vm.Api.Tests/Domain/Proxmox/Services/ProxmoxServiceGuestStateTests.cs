// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Tests.Support;

namespace Player.Vm.Api.Tests.Domain.Proxmox.Services;

/// <summary>
/// <c>ProxmoxService.GetGuestState</c>, the per-Vm read behind the columns the state poller keeps beyond
/// power state. Driven over <see cref="FakeProxmoxCluster"/>, so what is asserted is the request Proxmox
/// would have received and the JSON it would have answered with.
/// </summary>
public class ProxmoxServiceGuestStateTests
{
    private const int Vmid = 100;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region The current snapshot

    /// <summary>
    /// The config's <c>parent</c> is the snapshot the running state derives from, and its presence is what
    /// "has a snapshot" means: PVE's snapshot list always carries a synthetic <c>current</c> entry, so it
    /// cannot answer that question on its own, and the config read is one request rather than a list.
    /// </summary>
    [Fact]
    public async Task AConfigWithAParent_ReportsItAsTheCurrentSnapshot()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", """{"name":"vm","parent":"before-patching"}""");

        var state = await cluster.Service().GetGuestState(info, running: false, Ct);

        Assert.Equal("before-patching", state.CurrentSnapshot);
        Assert.True(state.HasSnapshot);
    }

    /// <summary>PVE leaves the key out for a Vm with no snapshot.</summary>
    [Fact]
    public async Task AConfigWithNoParent_ReportsNoSnapshot()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", """{"name":"vm"}""");

        var state = await cluster.Service().GetGuestState(info, running: false, Ct);

        Assert.Null(state.CurrentSnapshot);
        Assert.False(state.HasSnapshot);
    }

    /// <summary>A container's config is read from the LXC route, which carries the same <c>parent</c>.</summary>
    [Fact]
    public async Task AContainer_IsReadFromTheLxcConfig()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", """{"hostname":"ct","parent":"snap1"}""");

        var state = await cluster.Service().GetGuestState(info, running: false, Ct);

        Assert.Equal("snap1", state.CurrentSnapshot);
        Assert.Equal(
            $"api2/json/nodes/{FakeProxmoxCluster.DefaultNode}/lxc/{Vmid}/config",
            Assert.Single(cluster.Http.Paths));
    }

    /// <summary>
    /// The read is the current config, not the pending one, and is addressed to the node it was given
    /// without resolving it - the poller has just read that node from /cluster/resources.
    /// </summary>
    [Fact]
    public async Task TheConfigIsReadOnceFromTheGivenNodeWithoutResolvingIt()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, node: "pve3");
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", "{}");

        await cluster.Service().GetGuestState(info, running: false, Ct);

        var request = Assert.Single(cluster.Http.Sent);
        Assert.Equal($"api2/json/nodes/pve3/qemu/{Vmid}/config", request.Path);
        Assert.Equal("?current=1", request.Query);
    }

    /// <summary>
    /// A refused read is null rather than "no snapshot", so the poller keeps what it had instead of
    /// clearing it on a transient failure.
    /// </summary>
    [Fact]
    public async Task WhenTheConfigCannotBeRead_AnswersNull()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Rejects($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", "Configuration file does not exist");

        Assert.Null(await cluster.Service().GetGuestState(info, running: false, Ct));
        Assert.DoesNotContain(cluster.Http.Sent, x => x.Method != HttpMethod.Get);
    }

    #endregion

    #region IP addresses

    private const string Interfaces = "/agent/network-get-interfaces";

    /// <summary>
    /// What QGA's network-get-interfaces sends for a typical Linux guest: loopback, and one NIC carrying an
    /// IPv4, a global IPv6 and a link-local IPv6 address.
    /// </summary>
    private const string AgentInterfaces = """
        {"result":[
          {"name":"lo","hardware-address":"00:00:00:00:00:00","ip-addresses":[
            {"ip-address":"127.0.0.1","ip-address-type":"ipv4","prefix":8},
            {"ip-address":"::1","ip-address-type":"ipv6","prefix":128}]},
          {"name":"eth0","hardware-address":"bc:24:11:00:00:01","ip-addresses":[
            {"ip-address":"10.0.0.5","ip-address-type":"ipv4","prefix":24},
            {"ip-address":"2001:db8::5","ip-address-type":"ipv6","prefix":64},
            {"ip-address":"fe80::be24:11ff:fe00:1","ip-address-type":"ipv6","prefix":64}]}]}
        """;

    /// <summary>
    /// A running QEMU Vm with the agent on reports the guest's addresses, bare as vSphere's are stored,
    /// with loopback and link-local left out.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("enabled=1,fstrim_cloned_disks=1")]
    [InlineData("1,type=virtio")]
    public async Task ARunningQemuVmWithTheAgentOn_ReportsTheGuestsAddresses(string agent)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", $$"""{"agent":"{{agent}}"}""");
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, Interfaces)}", AgentInterfaces);

        var state = await cluster.Service().GetGuestState(info, running: true, Ct);

        Assert.Equal(["10.0.0.5", "2001:db8::5"], state.IpAddresses);
    }

    /// <summary>
    /// The agent is not asked at all when the config does not turn it on, or the Vm is off: PVE refuses
    /// both, and the poller would otherwise make that refused call for every such Vm on every pass.
    /// </summary>
    [Theory]
    [InlineData("""{"agent":"0"}""", true)]
    [InlineData("""{"agent":"enabled=0"}""", true)]
    [InlineData("{}", true)]
    [InlineData("""{"agent":"1"}""", false)]
    public async Task WithTheAgentOffOrTheVmStopped_TheAgentIsNotAskedAndThereAreNoAddresses(
        string config, bool running)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", config);

        var state = await cluster.Service().GetGuestState(info, running, Ct);

        Assert.Empty(state.IpAddresses);
        Assert.Equal([FakeProxmoxCluster.VmPath(info, "/config")], cluster.Http.Paths);
    }

    /// <summary>
    /// An agent configured but not running in the guest - a guest still booting, or one without the agent
    /// installed - is refused by PVE. That is no addresses, not an exception.
    /// </summary>
    [Fact]
    public async Task WhenTheAgentIsNotRunning_ThereAreNoAddressesAndNothingThrows()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", """{"agent":"1"}""");
        cluster.Http.AnswersJson(
            $"GET {FakeProxmoxCluster.VmPath(info, Interfaces)}",
            """{"data":null,"message":"QEMU guest agent is not running\n"}""",
            System.Net.HttpStatusCode.InternalServerError);

        var state = await cluster.Service().GetGuestState(info, running: true, Ct);

        Assert.Empty(state.IpAddresses);
        Assert.Null(state.CurrentSnapshot);
    }

    /// <summary>
    /// A call that fails in transport is turned into a failed Result by the Proxmox client, so it reads as a
    /// refusal - no addresses - rather than throwing out of the poller's pass. The snapshot, which came from
    /// the config read, still stands.
    /// </summary>
    [Fact]
    public async Task WhenTheAgentQueryFailsInTransport_ThereAreNoAddressesAndNothingThrows()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", """{"agent":"1","parent":"s1"}""");
        cluster.Http.Throws($"GET {FakeProxmoxCluster.VmPath(info, Interfaces)}");

        var state = await cluster.Service().GetGuestState(info, running: true, Ct);

        Assert.Empty(state.IpAddresses);
        Assert.True(state.HasSnapshot);
    }

    /// <summary>
    /// A container is read from its own interfaces route, which needs no agent. Newer PVE lists
    /// <c>ip-addresses</c> per interface the way QGA does.
    /// </summary>
    [Fact]
    public async Task ARunningContainer_ReportsItsInterfaceAddresses()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", """{"hostname":"ct"}""");
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, "/interfaces")}",
            """
            [{"name":"lo","hwaddr":"00:00:00:00:00:00","inet":"127.0.0.1/8","inet6":"::1/128","ip-addresses":[
               {"ip-address":"127.0.0.1","ip-address-type":"inet","prefix":"8"}]},
             {"name":"eth0","hwaddr":"bc:24:11:00:00:02","inet":"10.0.0.6/24","inet6":"fe80::1/64","ip-addresses":[
               {"ip-address":"10.0.0.6","ip-address-type":"inet","prefix":"24"},
               {"ip-address":"fe80::1","ip-address-type":"inet6","prefix":"64"}]}]
            """);

        var state = await cluster.Service().GetGuestState(info, running: true, Ct);

        Assert.Equal(["10.0.0.6"], state.IpAddresses);
    }

    /// <summary>Older PVE sends only <c>inet</c> and <c>inet6</c>, in CIDR form.</summary>
    [Fact]
    public async Task ARunningContainerOnOlderPve_ReportsItsInetAddresses()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", "{}");
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, "/interfaces")}",
            """
            [{"name":"lo","hwaddr":"00:00:00:00:00:00","inet":"127.0.0.1/8","inet6":"::1/128"},
             {"name":"eth0","hwaddr":"bc:24:11:00:00:02","inet":"10.0.0.6/24","inet6":"2001:db8::6/64"}]
            """);

        var state = await cluster.Service().GetGuestState(info, running: true, Ct);

        Assert.Equal(["10.0.0.6", "2001:db8::6"], state.IpAddresses);
    }

    /// <summary>A stopped container has no interfaces to read, and is not asked.</summary>
    [Fact]
    public async Task AStoppedContainer_IsNotAskedForItsInterfaces()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC, status: "stopped");
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", "{}");

        var state = await cluster.Service().GetGuestState(info, running: false, Ct);

        Assert.Empty(state.IpAddresses);
        Assert.Single(cluster.Http.Paths);
    }

    [Theory]
    [InlineData(new[] { "10.0.0.5" }, new[] { "10.0.0.5" })]
    [InlineData(new[] { "10.0.0.5/24", "10.0.0.5" }, new[] { "10.0.0.5" })]
    [InlineData(new[] { "127.0.0.1", "::1", "169.254.10.1", "fe80::1%eth0" }, new string[0])]
    [InlineData(new[] { "2001:db8::5%2", "not-an-address", "", null }, new[] { "2001:db8::5" })]
    [InlineData(new[] { "192.168.1.9", "10.0.0.5" }, new[] { "192.168.1.9", "10.0.0.5" })]
    public void CollectAddresses_KeepsRoutableAddressesInOrderWithoutDuplicates(string[] given, string[] expected) =>
        Assert.Equal(expected, ProxmoxService.CollectAddresses(given));

    #endregion

    #region Cancellation

    /// <summary>
    /// A cancelled read stops waiting on a Proxmox call that has not answered, rather than waiting it
    /// out. The client takes no cancellation token, so this is what lets the state poller's stop end the
    /// read - and dispose its scope - before the host disposes the container under it.
    /// </summary>
    [Fact]
    public async Task ACancelledRead_StopsWaitingOnAConfigReadThatHasNotAnswered()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Http.Stalls($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", stalled.Task);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        try
        {
            var read = cluster.Service().GetGuestState(info, running: false, cancellation.Token);
            await PollLoop.Until(() => cluster.Http.Paths.Count == 1, "the config read to be sent");
            Assert.False(read.IsCompleted);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }
        finally
        {
            stalled.SetResult();
        }
    }

    /// <summary>
    /// The same for a guest-agent query, which would otherwise hold the read for its whole
    /// GuestAgentTimeoutMilliseconds - here a minute, so only the cancellation can end it in time.
    /// </summary>
    [Fact]
    public async Task ACancelledRead_StopsWaitingOnAnAgentThatHasNotAnswered()
    {
        var cluster = new FakeProxmoxCluster();
        cluster.Options.GuestAgentTimeoutMilliseconds = 60_000;
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, "/config")}", """{"agent":"1"}""");
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Http.Stalls($"GET {FakeProxmoxCluster.VmPath(info, Interfaces)}", stalled.Task);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        try
        {
            var read = cluster.Service().GetGuestState(info, running: true, cancellation.Token);
            await PollLoop.Until(() => cluster.Http.Paths.Count == 2, "the agent query to be sent");

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }
        finally
        {
            stalled.SetResult();
        }
    }

    #endregion
}
