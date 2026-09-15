// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// One message a hub sent, and who it was addressed to.
/// </summary>
/// <param name="Groups">The group names the send was addressed to, in the order the hub named them.</param>
/// <param name="Method">The client method name.</param>
/// <param name="Args">The arguments, as <c>SendAsync</c> packed them.</param>
internal sealed record HubSend(IReadOnlyList<string> Groups, string Method, object[] Args);

/// <summary>
/// One group membership change a hub made.
/// </summary>
internal sealed record GroupChange(string ConnectionId, string GroupName);

/// <summary>
/// Drives a <see cref="Hub"/> by direct invocation, recording the group names it joins and leaves and
/// the messages it addresses to groups.
/// </summary>
/// <remarks>
/// <para>
/// The group names are the point. Both hubs compute their own names - <c>ActiveConsoles-{userId}-{id}</c>,
/// <c>CurrentVirtualMachineUsers-{groupId}-{vmId}</c>, and for <c>JoinView</c> a bare group guid - and
/// those names are the whole contract between a subscribing client and whatever broadcasts to it: the
/// entity-event handlers in <c>VmUpdatedSignalRHandler</c>, and the task pollers for
/// <c>ProgressHub</c>. Nothing else in the application would notice a name changing on one side only,
/// and a real SignalR client cannot see a name at all - only whether a message arrived. So the names
/// are asserted here, through <see cref="IGroupManager"/>, and
/// <c>HubConnectionTests</c> proves separately that the two sides agree over a real connection.
/// </para>
/// <para>
/// <see cref="IGroupManager"/> is hand-written rather than substituted because a test wants the whole
/// set of names in order, which reads better than a sequence of <c>Received()</c> calls, and because
/// recording the connection id alongside each name is what shows a hub adding <em>its own</em>
/// connection. <see cref="IHubCallerClients"/> is substituted instead: it has a dozen members that
/// change between framework versions, and only the two group-addressed ones are ever reached.
/// </para>
/// </remarks>
internal sealed class HubHarness
{
    /// <summary>
    /// The connection id a harness reports unless it is given one. A test that needs two connections -
    /// two tabs of the same user, say - has to name them, because the hub's own unset only fires for the
    /// connection that set the presence.
    /// </summary>
    public const string DefaultConnectionId = "test-connection";

    private readonly List<HubSend> _sends = [];
    private readonly RecordingGroupManager _groups = new();

    public HubHarness(Guid userId, string userName = "test-user", string connectionId = DefaultConnectionId)
    {
        UserId = userId;
        UserName = userName;
        ConnectionId = connectionId;

        Context = Substitute.For<HubCallerContext>();
        Context.ConnectionId.Returns(connectionId);
        Context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", userId.ToString()), new Claim("name", userName)], "Test")));

        Clients = Substitute.For<IHubCallerClients>();
        Clients.Groups(Arg.Any<IReadOnlyList<string>>())
            .Returns(call => ProxyFor(call.Arg<IReadOnlyList<string>>()));
        Clients.Group(Arg.Any<string>())
            .Returns(call => ProxyFor([call.Arg<string>()]));
    }

    public Guid UserId { get; }
    public string UserName { get; }
    public string ConnectionId { get; }

    public HubCallerContext Context { get; }
    public IHubCallerClients Clients { get; }

    /// <summary>Group names added, in order.</summary>
    public IReadOnlyList<string> Added => _groups.Added.Select(x => x.GroupName).ToArray();

    /// <summary>Group names removed, in order.</summary>
    public IReadOnlyList<string> Removed => _groups.Removed.Select(x => x.GroupName).ToArray();

    /// <summary>Every add, with the connection id it was made for.</summary>
    public IReadOnlyList<GroupChange> AddedChanges => _groups.Added;

    /// <summary>Every message the hub addressed to a group, in order.</summary>
    public IReadOnlyList<HubSend> Sends => _sends;

    /// <summary>Every group name that received <paramref name="method"/>, deduplicated.</summary>
    public IReadOnlyCollection<string> Recipients(string method) =>
        _sends.Where(x => x.Method == method).SelectMany(x => x.Groups).Distinct().ToArray();

    /// <summary>Hands the hub this harness's context, clients and group manager.</summary>
    public T Attach<T>(T hub) where T : Hub
    {
        hub.Context = Context;
        hub.Clients = Clients;
        hub.Groups = _groups;

        return hub;
    }

    private IClientProxy ProxyFor(IReadOnlyList<string> groups)
    {
        var proxy = Substitute.For<IClientProxy>();

        proxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _sends.Add(new HubSend(groups, call.ArgAt<string>(0), call.ArgAt<object[]>(1)));
                return Task.CompletedTask;
            });

        return proxy;
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<GroupChange> Added { get; } = [];
        public List<GroupChange> Removed { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        {
            Added.Add(new GroupChange(connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        {
            Removed.Add(new GroupChange(connectionId, groupName));
            return Task.CompletedTask;
        }
    }
}
