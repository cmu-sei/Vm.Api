// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// Records what something outside a hub broadcasts through <see cref="IHubContext{THub}"/>: the entity
/// event handlers, and the task pollers.
/// </summary>
/// <remarks>
/// <para>
/// The other end of <see cref="HubHarness"/>. That one records the group names a hub <em>joins</em>; this
/// one records the names the application <em>sends to</em>, and the two together are what say a subscriber
/// and a broadcaster agree on a string. Neither is observable from a real client, which sees only whether
/// a message arrived - see <c>HubConnectionTests</c> for that half.
/// </para>
/// <para>
/// <see cref="IHubClients"/> is substituted rather than hand-written for the reason
/// <see cref="HubHarness"/> substitutes its own: nine members that shift between framework versions, of
/// which the callers here reach two. A member left unstubbed returns null and fails loudly at the call
/// site, which is the behaviour wanted - a handler that starts addressing clients some other way should
/// not quietly record nothing.
/// </para>
/// </remarks>
internal sealed class HubContextHarness<THub>
    where THub : Hub
{
    /// <summary>
    /// The recorded group name for a send to <c>Clients.All</c>. Not a valid group name, so it cannot
    /// collide with one a caller computes: every real name here is a guid.
    /// </summary>
    /// <remarks>
    /// A sentinel rather than a separate list because the interesting assertions are about who was told,
    /// and "everyone" is an answer to that question. <c>VmDeletedSignalRHandler</c> falls back to it when
    /// it cannot work out any group, so a test has to be able to tell that apart from telling nobody.
    /// </remarks>
    public const string Everyone = "<all clients>";

    private readonly List<HubSend> _sends = [];
    private readonly Dictionary<string, Exception> _failures = [];

    public HubContextHarness()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(call => ProxyFor(call.Arg<string>()));
        clients.All.Returns(_ => ProxyFor(Everyone));

        Context = Substitute.For<IHubContext<THub>>();
        Context.Clients.Returns(clients);
    }

    public IHubContext<THub> Context { get; }

    /// <summary>Every message sent, in order.</summary>
    public IReadOnlyList<HubSend> Sends => _sends;

    /// <summary>The messages sent as <paramref name="method"/>, in order.</summary>
    public IReadOnlyList<HubSend> Of(string method) =>
        _sends.Where(x => x.Method == method).ToArray();

    /// <summary>
    /// Makes every send to one group throw, for the callers that broadcast to each group in turn inside a
    /// <c>try</c> and have to be shown that one dead group does not cost the others theirs.
    /// </summary>
    /// <remarks>
    /// Offered here because a test cannot arrange it from outside: <c>Clients.Group</c> is stubbed with a
    /// lambda that builds and configures a fresh <see cref="IClientProxy"/> per call, so re-stubbing one
    /// group name from a test consumes NSubstitute's pending-call state inside that lambda and fails with
    /// "Could not find a call to return from". The failing send is not recorded, because it did not
    /// happen - <see cref="Recipients"/> means "was told", not "was addressed".
    /// </remarks>
    public void FailsFor(string group, Exception failure) => _failures[group] = failure;

    /// <summary>
    /// Every group name that received <paramref name="method"/>, in the order first addressed.
    /// </summary>
    /// <remarks>
    /// Deduplicated, because a handler sending the same message to one group twice is a different
    /// question from which groups were told - <see cref="Sends"/> is where that one is asked.
    /// </remarks>
    public IReadOnlyList<string> Recipients(string method) =>
        Of(method).SelectMany(x => x.Groups).Distinct().ToArray();

    private IClientProxy ProxyFor(string group)
    {
        var proxy = Substitute.For<IClientProxy>();

        proxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (_failures.TryGetValue(group, out var failure))
                {
                    throw failure;
                }

                _sends.Add(new HubSend([group], call.ArgAt<string>(0), call.ArgAt<object[]>(1)));
                return Task.CompletedTask;
            });

        return proxy;
    }
}
