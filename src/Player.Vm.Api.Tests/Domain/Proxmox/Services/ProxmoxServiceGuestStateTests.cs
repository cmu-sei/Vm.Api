// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
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
}
