// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Player.Vm.Api.Tests.Support;

/// <summary>One message a hub or hub context sent, and the groups it was addressed to.</summary>
/// <param name="Groups">The group names the send was addressed to, in the order the sender named them.</param>
/// <param name="Method">The client method name.</param>
/// <param name="Args">The arguments, as <c>SendAsync</c> packed them.</param>
internal sealed record HubSend(IReadOnlyList<string> Groups, string Method, object[] Args);

/// <summary>
/// The shared <see cref="HubHarness"/>, with every message the hub sends recorded in one ordered log,
/// each with the list of groups it was addressed to.
/// </summary>
/// <remarks>
/// For <c>VmHubPresenceTests</c> only. The presence broadcasts are asserted by their order across groups
/// (the first and the last <c>ActiveVirtualMachine</c> a hub sent, whichever groups they went to) and by
/// whether one send named several groups, which the shared harness's per-group proxies and
/// <c>Sent&lt;T&gt;</c> do not express. The group-membership tests use the shared harness directly
/// (<c>JoinedGroups</c>, <c>LeftGroups</c>).
/// </remarks>
internal sealed class RecordingHubHarness
{
    private readonly List<HubSend> _sends = [];

    public RecordingHubHarness(Guid userId, string userName = "test-user", string connectionId = HubHarness.ConnectionId)
    {
        Harness = new HubHarness(
            userId, new ClaimsPrincipalBuilder().WithUserId(userId).WithName(userName).Build(), connectionId);
        UserName = userName;

        Harness.Clients.Groups(Arg.Any<IReadOnlyList<string>>())
            .Returns(call => ProxyFor(call.Arg<IReadOnlyList<string>>()));
        Harness.Clients.Group(Arg.Any<string>())
            .Returns(call => ProxyFor([call.Arg<string>()]));
    }

    /// <summary>The shared harness underneath, for what this class does not wrap.</summary>
    public HubHarness Harness { get; }

    public Guid UserId => Harness.UserId;

    public string UserName { get; }

    /// <summary>Every message the hub sent to a group or a list of groups, in order.</summary>
    public IReadOnlyList<HubSend> Sends => _sends;

    /// <summary>The groups told <paramref name="method"/>, each once.</summary>
    public IReadOnlyCollection<string> Recipients(string method) =>
        [.. _sends.Where(x => x.Method == method).SelectMany(x => x.Groups).Distinct()];

    public T Attach<T>(T hub) where T : Hub => Harness.Attach(hub);

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
}
