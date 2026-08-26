// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The two hubs as endpoints of the running application: where they are mapped, who may reach them, and -
/// for <c>ProgressHub</c>, which needs no database - one round trip over a real SignalR connection.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of <see cref="ProgressHubTests"/> and <see cref="VmHubGroupTests"/>, which
/// invoke the hub methods directly to assert the group names. A real client cannot see a name, only
/// whether a message arrived; what it can see, and those cannot, is that the endpoint exists at the path
/// clients dial, that it refuses an anonymous caller, and that a broadcast addressed to the name a client
/// joined actually reaches it.
/// </para>
/// <para>
/// <c>VmHub</c> is deliberately not driven over a connection here. Its methods resolve a
/// <c>VmContext</c>, and a hub invocation is not an HTTP request, so <see cref="VmApiFactory"/> cannot
/// route it to this test's database - it would silently read the host's. That is why the hub's own
/// behaviour is tested by direct invocation and only its edge is tested here.
/// </para>
/// </remarks>
public class HubConnectionTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    private const string VmHubPath = "/hubs/vm";
    private const string ProgressHubPath = "/hubs/progress";

    private readonly List<HubConnection> _connections = [];

    public override async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    #region Where the hubs are mapped

    public static TheoryData<string> EveryHubPath => new() { VmHubPath, ProgressHubPath };

    /// <summary>
    /// Both hubs require authorization, so an anonymous client is refused before it ever gets a
    /// connection id. This is the only gate either hub has: neither checks anything about the caller
    /// afterwards except <c>VmHub.JoinUser</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryHubPath))]
    public async Task Negotiate_WithoutCredentials_Is401(string path)
    {
        var response = await AnonymousClient.PostAsync($"{path}/negotiate?negotiateVersion=1", null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(EveryHubPath))]
    public async Task Negotiate_WhenAuthenticated_HandsOutAConnection(string path)
    {
        var response = await Client.PostAsync($"{path}/negotiate?negotiateVersion=1", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("connectionToken", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The paths themselves, which no test that dials them could distinguish from a typo agreed on by
    /// both sides. These two strings are also written into the Angular client's own configuration.
    /// </summary>
    [Theory]
    [InlineData(VmHubPath, typeof(VmHub))]
    [InlineData(ProgressHubPath, typeof(ProgressHub))]
    public void EachHub_IsMappedAtThePathClientsDial(string path, Type hubType)
    {
        Assert.Equal(hubType, MappedHubs().Single(x => x.Path == path).HubType);
    }

    /// <summary>
    /// Every endpoint a hub is mapped as. A <c>MapHub</c> produces one per transport as well as the
    /// negotiate route, and authorization applies to each of them separately, so all of them are checked:
    /// a single unprotected transport is a way in.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryHubPath))]
    public void EveryEndpointOfEachHub_RequiresAuthorization(string path)
    {
        var endpoints = HubEndpoints().Where(x => Path(x) == path).ToArray();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, x => Assert.NotEmpty(x.Metadata.GetOrderedMetadata<IAuthorizeData>()));
    }

    /// <summary>
    /// <c>Player.Vm.Api.Hubs.VmHub</c> is a second copy of the hub, with its own copy of
    /// <c>VmHubMethods</c>. Nothing maps it and nothing imports its namespace, so it is unreachable: the
    /// hub clients talk to is the one under <c>Features/Vms/Hubs</c>.
    /// </summary>
    /// <remarks>
    /// Recorded rather than deleted, because deleting a public type is a decision for whoever owns the
    /// file. What makes it worth a test is that the two copies are similar enough to edit by mistake -
    /// a change made to the unmapped one would compile, pass every other test, and do nothing.
    /// </remarks>
    [Fact]
    public void TheDuplicateVmHub_IsMappedNowhere()
    {
        Assert.DoesNotContain(typeof(Player.Vm.Api.Hubs.VmHub), MappedHubs().Select(x => x.HubType));
    }

    [Fact]
    public void TheApplication_MapsNoHubsButThoseTwo()
    {
        Assert.Equal<string>([ProgressHubPath, VmHubPath], MappedHubs().Select(x => x.Path).Order());
    }

    #endregion

    #region One round trip over a real connection

    /// <summary>
    /// The whole point of <c>ProgressHub</c>, end to end: a client joins the group named after a Vm, the
    /// application broadcasts a task update to that name - which is what the vSphere and Proxmox task
    /// pollers do, with <c>Clients.Group(vmId.ToString())</c> - and the message arrives.
    /// </summary>
    [Fact]
    public async Task AClientThatJoinedAVm_ReceivesWhatIsBroadcastToThatVm()
    {
        var vmId = Guid.NewGuid();
        var connection = await Connect(ProgressHubPath);
        var received = Received<string>(connection, "Progress");

        await connection.InvokeAsync("Join", vmId.ToString(), Ct);
        await Broadcast(vmId.ToString(), "Progress", "50");

        Assert.Equal("50", await Arrives(received));
    }

    /// <summary>
    /// The other half of it: the group is the only thing that decides who hears a broadcast, so a client
    /// subscribed to another Vm hears nothing. Without this, a hub that added every connection to every
    /// group would pass the test above.
    /// </summary>
    [Fact]
    public async Task AClientThatJoinedAnotherVm_ReceivesNothing()
    {
        var connection = await Connect(ProgressHubPath);
        var received = Received<string>(connection, "Progress");

        await connection.InvokeAsync("Join", Guid.NewGuid().ToString(), Ct);
        await Broadcast(Guid.NewGuid().ToString(), "Progress", "50");

        Assert.False(received.IsCompleted);
    }

    /// <summary>
    /// Leaving stops the messages, which is what a client navigating away from a Vm does.
    /// </summary>
    [Fact]
    public async Task AClientThatLeft_ReceivesNothingMore()
    {
        var vmId = Guid.NewGuid();
        var connection = await Connect(ProgressHubPath);

        await connection.InvokeAsync("Join", vmId.ToString(), Ct);
        var first = Received<string>(connection, "Progress");
        await Broadcast(vmId.ToString(), "Progress", "50");
        Assert.Equal("50", await Arrives(first));

        await connection.InvokeAsync("Leave", vmId.ToString(), Ct);
        var second = Received<string>(connection, "Progress");
        await Broadcast(vmId.ToString(), "Progress", "100");

        Assert.False(second.IsCompleted);
    }

    /// <summary>
    /// A hub method that does not exist fails the invocation rather than the connection, which is what
    /// makes the method names part of the contract: <c>Join</c> and <c>Leave</c> are the only two.
    /// </summary>
    [Fact]
    public async Task ProgressHub_HasNoMethodsBesidesJoinAndLeave()
    {
        var connection = await Connect(ProgressHubPath);

        var error = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinAll", Ct));

        Assert.Contains("JoinAll", error.Message);
        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A connected client, over the in-process server rather than a socket.
    /// </summary>
    /// <remarks>
    /// Long polling, because a <c>TestServer</c> has no real WebSocket for the client transport to
    /// upgrade to; the negotiation and the hub protocol above it are the real ones either way. The
    /// user id header is what <see cref="TestAuthHandler"/> authenticates, and it has to be set here
    /// rather than on a client because the connection makes its own requests.
    /// </remarks>
    private async Task<HubConnection> Connect(string path)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{path}", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Headers.Add(TestAuthHandler.UserIdHeader, Factory.UserId.ToString());
            })
            .Build();

        _connections.Add(connection);

        await connection.StartAsync(Ct);

        return connection;
    }

    /// <summary>
    /// The first argument of the next <paramref name="method"/> the server sends. Registered before the
    /// broadcast, so nothing is missed, and awaited after it.
    /// </summary>
    private static Task<T> Received<T>(HubConnection connection, string method)
    {
        var received = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<T>(method, value => received.TrySetResult(value));

        return received.Task;
    }

    /// <summary>
    /// The value the server sent, or a failure rather than a hung run if it sent nothing.
    /// </summary>
    /// <remarks>
    /// This is the concrete form of the suite's cancellation-token rule. Awaiting a
    /// <c>TaskCompletionSource</c> that nothing will ever complete hangs the run instead of failing it,
    /// which is exactly what happened when the group name in <c>ProgressHub.Join</c> was mutated to check
    /// these tests fail: the run stopped rather than reddening. Ten seconds is far longer than an
    /// in-process broadcast needs and short enough that CI reports it.
    /// </remarks>
    private Task<T> Arrives<T>(Task<T> received) =>
        received.WaitAsync(TimeSpan.FromSeconds(10), Ct);

    /// <summary>
    /// Sends to a group as the application does, through the hub context the task pollers hold rather
    /// than through a hub method - which is the point: it is the other side of the name.
    /// </summary>
    private async Task Broadcast(string group, string method, object argument)
    {
        await Factory.Services.GetRequiredService<IHubContext<ProgressHub>>()
            .Clients.Group(group).SendAsync(method, argument, Ct);

        // The negative assertions need the absence of a message to mean something. A long-polling client
        // has to be given a chance to receive it, and one it did not subscribe to leaves nothing to wait
        // for, so this is the one place a delay is the only available signal.
        await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
    }

    private IEnumerable<RouteEndpoint> HubEndpoints() =>
        Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(x => x.Metadata.GetMetadata<HubMetadata>() is not null);

    /// <summary>
    /// Every hub the running application has mapped, as a path and the type behind it. Taken from the
    /// endpoints rather than from <c>Startup</c>, so a hub mapped anywhere is included.
    /// </summary>
    private IEnumerable<(string Path, Type HubType)> MappedHubs() =>
        HubEndpoints()
            .Select(x => (Path(x), x.Metadata.GetMetadata<HubMetadata>().HubType))
            .Distinct();

    /// <summary>
    /// The path a hub is dialed at. Each <c>MapHub</c> produces several endpoints - the negotiate route
    /// and one per transport - so the suffixes come off.
    /// </summary>
    private static string Path(RouteEndpoint endpoint) =>
        "/" + endpoint.RoutePattern.RawText.TrimStart('/').Replace("/negotiate", string.Empty);

    #endregion
}
