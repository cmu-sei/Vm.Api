// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Xunit;

namespace Crucible.Api.Testing;

/// <summary>
/// The connection-scoped state SignalR sets on a hub before invoking a method: who is calling, over which
/// connection, and the proxies the hub sends through.
/// </summary>
/// <remarks>
/// Hubs are only reachable through these three properties, so a hub test is a test of what the hub does to
/// them — which groups it joins and which proxy it sends a message to. The proxies are substitutes rather
/// than a real SignalR pipeline, and <see cref="Sent{T}"/> reads back the argument a
/// <c>SendAsync("Reply", x)</c> extension call turned into a <c>SendCoreAsync</c> call.
/// </remarks>
public sealed class HubHarness
{
    public const string ConnectionId = "connection-1";

    private readonly Dictionary<string, IClientProxy> _groups = [];

    /// <param name="userId">The caller's id, the <c>sub</c> claim. A new one when omitted.</param>
    /// <param name="user">
    /// The caller, when a hub method reads more than its id (permission claims from the app's
    /// <c>ClaimsPrincipalBuilder</c>). By default an authenticated principal with <c>sub</c> and
    /// <c>name</c> claims.
    /// </param>
    /// <param name="connectionId">
    /// The connection's id, <see cref="ConnectionId"/> by default; a test of two connections of one user
    /// builds a harness per connection with ids of its own.
    /// </param>
    public HubHarness(Guid? userId = null, ClaimsPrincipal user = null, string connectionId = ConnectionId)
    {
        UserId = userId ?? Guid.NewGuid();
        User = user ?? new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", UserId.ToString()), new Claim("name", "Test User")], "Test"));

        Clients.Caller.Returns(Caller);
        Clients.Group(Arg.Any<string>()).Returns(call => Group(call.Arg<string>()));
        Clients.Groups(Arg.Any<IReadOnlyList<string>>())
            .Returns(call => new FanOutProxy([.. call.Arg<IReadOnlyList<string>>().Select(Group)]));

        Context.ConnectionId.Returns(connectionId);
        Context.Items.Returns(Items);
        Context.User.Returns(User);
    }

    public Guid UserId { get; }

    /// <summary>The principal <c>Context.User</c> answers with.</summary>
    public ClaimsPrincipal User { get; }

    public IHubCallerClients Clients { get; } = Substitute.For<IHubCallerClients>();

    /// <summary>The connection that invoked the method — where a reply meant for one caller goes.</summary>
    public ISingleClientProxy Caller { get; } = Substitute.For<ISingleClientProxy>();

    public IGroupManager Groups { get; } = Substitute.For<IGroupManager>();

    public HubCallerContext Context { get; } = Substitute.For<HubCallerContext>();

    /// <summary>
    /// Per-connection state that outlives a single hub method — a hub can keep, say, a presence id here so
    /// that disconnecting can undo what joining did.
    /// </summary>
    /// <remarks>
    /// SignalR's own dictionary answers null for a key that was never set; a plain
    /// <see cref="Dictionary{TKey, TValue}"/> would throw instead and turn "leave without joining" into a
    /// failure that production does not have.
    /// </remarks>
    public IDictionary<object, object> Items { get; } = new ItemsFeature().Items;

    public T Attach<T>(T hub) where T : Hub
    {
        hub.Clients = Clients;
        hub.Groups = Groups;
        hub.Context = Context;
        return hub;
    }

    /// <summary>One proxy per group name, so a test can assert against the group it expects.</summary>
    public IClientProxy Group(string name)
    {
        if (!_groups.TryGetValue(name, out var proxy))
        {
            proxy = Substitute.For<IClientProxy>();
            _groups[name] = proxy;
        }

        return proxy;
    }

    /// <summary>
    /// The groups the hub added a connection to (<c>Groups.AddToGroupAsync</c>), in order. The connection
    /// id each was added for is in <see cref="Groups"/>' received calls.
    /// </summary>
    public IReadOnlyList<string> JoinedGroups => GroupChanges(nameof(IGroupManager.AddToGroupAsync));

    /// <summary>The groups the hub removed a connection from (<c>Groups.RemoveFromGroupAsync</c>), in order.</summary>
    public IReadOnlyList<string> LeftGroups => GroupChanges(nameof(IGroupManager.RemoveFromGroupAsync));

    /// <summary>The single argument sent to <paramref name="method"/>, or a failure if it was not sent once.</summary>
    public static T Sent<T>(IClientProxy proxy, string method)
    {
        var arguments = Assert.Single(Sends(proxy, method));

        return Assert.IsType<T>(Assert.Single(arguments));
    }

    public static void NothingSent(IClientProxy proxy, string method)
    {
        Assert.Empty(Sends(proxy, method));
    }

    /// <summary>
    /// The audiences the hub addressed, in order: each member of <see cref="Clients"/> it read
    /// (<c>Caller</c>, <c>Group</c>, <c>Groups</c>, <c>OthersInGroup</c>, <c>All</c>, <c>Others</c>,
    /// <c>Client</c>, <c>User</c>, ...), named as <c>nameof(IHubCallerClients.X)</c> names it.
    /// </summary>
    /// <remarks>
    /// The proxies are substitutes, so a hub that sends through an audience the test does not read (a
    /// broadcast to <c>All</c> where the test reads one group) passes a test that only asks what the group
    /// received. <see cref="AssertAddressedOnly"/> and <see cref="NothingAddressed"/> close that gap.
    /// </remarks>
    public IReadOnlyList<string> Addressed =>
        [.. Clients.ReceivedCalls()
            .Select(x => x.GetMethodInfo().Name)
            .Select(name => name.StartsWith("get_", StringComparison.Ordinal) ? name[4..] : name)];

    /// <summary>
    /// Fails unless every audience the hub addressed is one of <paramref name="members"/>
    /// (<c>nameof(IHubCallerClients.OthersInGroup)</c>, ...), and unless the hub left the connection alone:
    /// it read no <c>Context.Features</c> and did not abort it.
    /// </summary>
    public void AssertAddressedOnly(params string[] members)
    {
        Assert.All(Addressed, member => Assert.Contains(member, members));
        AssertConnectionUntouched();
    }

    /// <summary>Fails if the hub addressed any audience, read <c>Context.Features</c> or aborted the connection.</summary>
    public void NothingAddressed()
    {
        Assert.Empty(Addressed);
        AssertConnectionUntouched();
    }

    private void AssertConnectionUntouched() =>
        Assert.DoesNotContain(
            Context.ReceivedCalls(),
            x => x.GetMethodInfo().Name is "get_" + nameof(HubCallerContext.Features) or nameof(HubCallerContext.Abort));

    private IReadOnlyList<string> GroupChanges(string method) =>
        [.. Groups.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == method)
            .Select(x => (string)x.GetArguments()[1])];

    private static IEnumerable<object[]> Sends(IClientProxy proxy, string method) =>
        proxy.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))
            .Select(x => x.GetArguments())
            .Where(x => (string)x[0] == method)
            .Select(x => (object[])x[1]);

    /// <summary>
    /// A send to <c>Clients.Groups(list)</c>, passed to each group's proxy, which is where SignalR
    /// delivers it, so <see cref="Sent{T}"/> reads it from the group a test names.
    /// </summary>
    private sealed class FanOutProxy(IReadOnlyList<IClientProxy> proxies) : IClientProxy
    {
        public async Task SendCoreAsync(string method, object[] args, CancellationToken cancellationToken = default)
        {
            foreach (var proxy in proxies)
            {
                await proxy.SendCoreAsync(method, args, cancellationToken);
            }
        }
    }
}
