// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crucible.Api.Testing;

/// <summary>
/// Routes a request to the database of the test that made it.
/// </summary>
/// <remarks>
/// <para>
/// One host serves the whole run, so the application's own context registration cannot be reused:
/// <c>AddEventPublishingDbContextFactory</c> pools its options as a singleton, while each test owns an
/// isolated PostgreSQL database. The app factory calls <see cref="ReplaceRegistration{TContext}(IServiceCollection)"/>,
/// which registers the context with one that asks this class.
/// </para>
/// <para>
/// Each test registers its session under an id and sends that id as <see cref="HeaderName"/> on every
/// request. A header rather than an <c>AsyncLocal</c>: a lookup that misses then fails loudly and names
/// the request it could not route, where an ambient value that did not flow would silently resolve
/// another test's database.
/// </para>
/// </remarks>
internal static class TestDatabaseScope
{
    public const string HeaderName = "X-Test-Session";

    private static readonly ConcurrentDictionary<Guid, object> _sessions = new();

    public static void Register<TContext>(Guid id, ITestDatabaseSession<TContext> session)
        where TContext : DbContext => _sessions[id] = session;

    public static void Release(Guid id) => _sessions.TryRemove(id, out _);

    /// <summary>
    /// Replaces the application's registration of <typeparamref name="TContext"/> with one that resolves
    /// the database of the test that sent the request. Call it from <c>ConfigureTestServices</c>. A host
    /// whose startup resolves the context outside a request uses the two-argument overload instead.
    /// </summary>
    /// <remarks>
    /// The request scope is passed as the context's <c>ServiceProvider</c>, which is what the
    /// application's own registration does, so entity events reach the real handlers. The factory
    /// registration goes too: leaving it would let a stray resolution reach a pooled context bound to a
    /// database no test owns.
    /// </remarks>
    public static void ReplaceRegistration<TContext>(IServiceCollection services) where TContext : DbContext
    {
        services.RemoveAll<TContext>();
        services.RemoveAll<IDbContextFactory<TContext>>();

        services.AddScoped(provider => Resolve<TContext>(
                provider.GetRequiredService<IHttpContextAccessor>().HttpContext)
            .CreateContext(provider));
    }

    /// <summary>
    /// As <see cref="ReplaceRegistration{TContext}(IServiceCollection)"/>, and a resolution made outside
    /// any request gets a context over the session <paramref name="outsideARequest"/> returns, while it
    /// returns one. When it returns null, such a resolution throws as the one-argument overload's does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a host whose <c>Program.Main</c> runs <c>InitializeDatabase</c> (the factory templates' step 1B).
    /// <c>Main</c> resolves the context from a scope of its own while the host starts, with no request and
    /// so no header to route by. The factory passes the host's own throwaway session until the host has
    /// started, then null, so a stray resolution outside a request still fails loudly rather than writing
    /// to a database no test reads.
    /// </para>
    /// <para>
    /// The context is built over that session's own services (<see cref="ITestDatabaseSession{TContext}.CreateContext()"/>),
    /// not the resolving scope, so its entity events go to the session's substituted mediator. The seeding
    /// in <c>InitializeDatabase</c> then never reaches the application's event handlers or a test's hub
    /// recording (blueprint.api's seed broadcasts reached its recorders that way and made its tests flaky).
    /// </para>
    /// </remarks>
    public static void ReplaceRegistration<TContext>(
        IServiceCollection services,
        Func<ITestDatabaseSession<TContext>> outsideARequest) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(outsideARequest);

        services.RemoveAll<TContext>();
        services.RemoveAll<IDbContextFactory<TContext>>();

        services.AddScoped(provider =>
        {
            var request = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;

            if (request is null && outsideARequest() is { } host)
            {
                return host.CreateContext();
            }

            return Resolve<TContext>(request).CreateContext(provider);
        });
    }

    /// <summary>The session belonging to the test that made <paramref name="context"/>'s request.</summary>
    public static ITestDatabaseSession<TContext> Resolve<TContext>(HttpContext context) where TContext : DbContext
    {
        if (context is null)
        {
            throw new InvalidOperationException(
                $"A {typeof(TContext).Name} was resolved outside a request, where no {HeaderName} header " +
                "can say which test database to use. Resolve one from a request scope, or take a context " +
                "from DatabaseTestBase.NewContext.");
        }

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

        return (ITestDatabaseSession<TContext>)session;
    }
}
