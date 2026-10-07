// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Crucible.Api.Testing;

/// <summary>
/// Stands in for a hub context and keeps what the application broadcast, so that a test can read the
/// messages sent to one audience.
/// </summary>
/// <remarks>
/// <para>
/// Written out rather than substituted. A hub context registered by a run-wide app factory is
/// one instance for the whole run, and NSubstitute keeps assertion state per thread while
/// <c>TestServer</c> runs requests on the same pool the tests do: a pending <c>Received()</c> from one
/// test can swallow a broadcast made for another, and creating substitutes on several threads at once
/// crosses their bookkeeping. Either way calls go missing, which reads as a broadcast that never
/// happened — a flake that arrives with load rather than with a change.
/// </para>
/// <para>
/// Recording every audience by name also means a test names the group it expects, so a broadcast to
/// the wrong one fails rather than passing on the strength of <c>Clients</c> having been touched.
/// </para>
/// </remarks>
public sealed class HubRecorder<THub> : IHubContext<THub> where THub : Hub
{
    private readonly RecordingClients _clients;

    public HubRecorder() => _clients = new RecordingClients();

    public IHubClients Clients => _clients;

    /// <remarks>
    /// Nothing outside the hubs manages groups, and a hub is given SignalR's own manager rather than
    /// this one, so a call here is a question the harness cannot answer.
    /// </remarks>
    public IGroupManager Groups =>
        throw new NotSupportedException(
            $"Nothing in the application manages groups through IHubContext<{typeof(THub).Name}>. " +
            "Give the recorder a group manager if that has changed.");

    /// <summary>
    /// What was broadcast to a group, in the order it was sent: through <c>Clients.Group(name)</c>, and
    /// through <c>Clients.Groups(list)</c> naming it, which SignalR delivers to each group in the list.
    /// </summary>
    public IReadOnlyList<HubBroadcast> ToGroup(string groupName) => _clients.Recorded($"group:{groupName}");

    /// <summary>What was broadcast to a group, in the order it was sent.</summary>
    public IReadOnlyList<HubBroadcast> ToGroup(Guid groupName) => ToGroup(groupName.ToString());

    /// <summary>
    /// What was broadcast in one <c>Clients.Groups(list)</c> call naming exactly these groups, in this
    /// order. For a test about the call's shape; <see cref="ToGroup(string)"/> is what each group received.
    /// </summary>
    public IReadOnlyList<HubBroadcast> ToGroups(params string[] groupNames) =>
        _clients.Recorded($"groups:{string.Join(',', groupNames)}");

    /// <summary>What was broadcast to one user's connections (<c>Clients.User(id)</c>).</summary>
    public IReadOnlyList<HubBroadcast> ToUser(string userId) => _clients.Recorded($"user:{userId}");

    /// <summary>What was broadcast to one user's connections (<c>Clients.User(id)</c>).</summary>
    public IReadOnlyList<HubBroadcast> ToUser(Guid userId) => ToUser(userId.ToString());

    /// <summary>
    /// What was broadcast to every connection (<c>Clients.All</c>). On a run-wide recorder that is every
    /// test's broadcast, so read it only for a payload the test owns, or on a recorder one test owns.
    /// </summary>
    public IReadOnlyList<HubBroadcast> ToAll() => _clients.Recorded("all");

    /// <summary>
    /// The groups that received <paramref name="method"/>, in the order each was first sent it, each once.
    /// Asks "who was told"; <see cref="ToGroup(string)"/> asks what one group was told, and how often.
    /// </summary>
    public IReadOnlyList<string> Recipients(string method) => _clients.Recipients(method);

    /// <summary>
    /// Makes every send to <paramref name="groupName"/> throw <paramref name="failure"/>, for the callers
    /// that broadcast to each group in turn and must show that one failing group does not cost the others
    /// theirs. The failed send is not recorded, because it did not happen. On a recorder one test owns, or
    /// for a group named by an id the test owns.
    /// </summary>
    public void FailsFor(string groupName, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        _clients.Fail($"group:{groupName}", failure);
    }

    /// <summary>
    /// One proxy per audience, kept for the life of the run so that a test reads the same recorder the
    /// request wrote to.
    /// </summary>
    /// <remarks>
    /// The keys are namespaced because a group and a user are named by the same ids — a team's group and
    /// a user's connection would otherwise share a proxy and each see the other's messages.
    /// </remarks>
    private sealed class RecordingClients : IHubClients
    {
        private readonly ConcurrentDictionary<string, RecordingClientProxy> _proxies = new();
        private readonly ConcurrentDictionary<string, Exception> _failures = new();

        /// <summary>Every recorded send, in order, keyed by audience, for <see cref="Recipients"/>.</summary>
        private readonly ConcurrentQueue<(string Key, string Method)> _log = new();

        public IClientProxy All => Proxy("all");

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
            Proxy($"all-except:{string.Join(',', excludedConnectionIds)}");

        public IClientProxy Client(string connectionId) => Proxy($"client:{connectionId}");

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) =>
            Proxy($"clients:{string.Join(',', connectionIds)}");

        public IClientProxy Group(string groupName) => Proxy($"group:{groupName}");

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            Proxy($"group-except:{groupName}:{string.Join(',', excludedConnectionIds)}");

        /// <summary>
        /// Recorded as one send to the list and one to each group in it, which is what each group's
        /// connections receive.
        /// </summary>
        public IClientProxy Groups(IReadOnlyList<string> groupNames) =>
            new FanOutProxy([Proxy($"groups:{string.Join(',', groupNames)}"), .. groupNames.Select(Group)]);

        public IClientProxy User(string userId) => Proxy($"user:{userId}");

        public IClientProxy Users(IReadOnlyList<string> userIds) =>
            Proxy($"users:{string.Join(',', userIds)}");

        public IReadOnlyList<HubBroadcast> Recorded(string key) =>
            _proxies.TryGetValue(key, out var proxy) ? proxy.Messages : [];

        public IReadOnlyList<string> Recipients(string method) =>
            [.. _log
                .Where(x => x.Method == method && x.Key.StartsWith("group:", StringComparison.Ordinal))
                .Select(x => x.Key["group:".Length..])
                .Distinct()];

        public void Fail(string key, Exception failure) => _failures[key] = failure;

        private RecordingClientProxy Proxy(string key) =>
            _proxies.GetOrAdd(key, _ => new RecordingClientProxy(key, this));

        public void Record(string key, string method)
        {
            if (_failures.TryGetValue(key, out var failure))
            {
                throw failure;
            }

            _log.Enqueue((key, method));
        }
    }

    private sealed class RecordingClientProxy(string key, RecordingClients clients) : IClientProxy
    {
        private readonly ConcurrentQueue<HubBroadcast> _messages = new();

        public IReadOnlyList<HubBroadcast> Messages => [.. _messages];

        public Task SendCoreAsync(
            string method,
            object[] args,
            CancellationToken cancellationToken = default)
        {
            clients.Record(key, method);
            _messages.Enqueue(new HubBroadcast(method, args ?? []));

            return Task.CompletedTask;
        }
    }

    private sealed class FanOutProxy(IReadOnlyList<IClientProxy> proxies) : IClientProxy
    {
        public async Task SendCoreAsync(
            string method,
            object[] args,
            CancellationToken cancellationToken = default)
        {
            foreach (var proxy in proxies)
            {
                await proxy.SendCoreAsync(method, args, cancellationToken);
            }
        }
    }
}

/// <summary>One message a hub sent: the client method it named, and what it carried.</summary>
public sealed record HubBroadcast(string Method, object[] Arguments)
{
    /// <summary>The single argument of a broadcast that sends one.</summary>
    public object Argument => Arguments.Length == 1
        ? Arguments[0]
        : throw new InvalidOperationException(
            $"'{Method}' was sent with {Arguments.Length} arguments, so name the one you mean.");
}
