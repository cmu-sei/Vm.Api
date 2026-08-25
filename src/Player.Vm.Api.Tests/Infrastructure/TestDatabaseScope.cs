// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// Routes a request to the database of the test that made it.
/// </summary>
/// <remarks>
/// <para>
/// The application's own <see cref="Player.Vm.Api.Data.VmContext"/> registration cannot be reused for
/// this: <c>AddEventPublishingDbContextFactory</c> builds one pooled set of <c>DbContextOptions</c>
/// when the container is built, with one connection string baked in, so there is no point at which a
/// per-request database could be chosen. <see cref="VmApiFactory"/> replaces the scoped registration
/// with one that asks this class.
/// </para>
/// <para>
/// A header rather than an <c>AsyncLocal</c>: a lookup that misses then fails loudly and names the
/// request it could not route, where an ambient value that failed to flow across a thread hop would
/// silently resolve some other test's database.
/// </para>
/// </remarks>
internal static class TestDatabaseScope
{
    public const string HeaderName = "X-Test-Session";

    private static readonly ConcurrentDictionary<Guid, TestDatabaseSession> _sessions = new();

    public static void Register(Guid id, TestDatabaseSession session) => _sessions[id] = session;

    public static void Release(Guid id) => _sessions.TryRemove(id, out _);

    /// <summary>
    /// The session belonging to the test that made <paramref name="context"/>'s request.
    /// </summary>
    public static TestDatabaseSession Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var value = context.Request.Headers[HeaderName].ToString();

        if (!Guid.TryParse(value, out var id))
        {
            throw new InvalidOperationException(
                $"{context.Request.Method} {context.Request.Path} carries no usable {HeaderName} " +
                $"header (found '{value}'). Send requests with a client from ApiTestBase, which sets it.");
        }

        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new InvalidOperationException(
                $"{HeaderName} '{id}' names no registered test database, so the test that owns it has " +
                "already torn down. A request outlived the test that made it; await it before the test " +
                "returns.");
        }

        return session;
    }
}
