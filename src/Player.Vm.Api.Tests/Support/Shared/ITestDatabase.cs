// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Crucible.Api.Testing;

/// <summary>
/// Hands out an isolated database per test. The application's <c>DatabaseFixture</c> implements this,
/// which is what <see cref="DatabaseTestBase{TContext}"/> and <see cref="ApiTestBase{TContext}"/> ask.
/// </summary>
public interface ITestDatabaseSessionSource<TContext> where TContext : DbContext
{
    /// <summary>Creates a database no other test can see, cloned from the migrated template.</summary>
    Task<ITestDatabaseSession<TContext>> BeginSessionAsync();
}

/// <summary>
/// One test's isolated database. Disposing it drops the database.
/// </summary>
/// <remarks>
/// Isolation deliberately avoids wrapping the test in a rolled-back transaction. The
/// <c>EntityEventInterceptor</c> of <c>Crucible.Common.EntityEvents</c> publishes entity events on
/// <c>TransactionCommitted</c> when a transaction is in progress and clears its tracked state on
/// <c>TransactionRolledBack</c>, so transaction-based isolation would silently stop entity events from
/// firing. Each test therefore gets its own PostgreSQL database.
/// </remarks>
public interface ITestDatabaseSession<TContext> : IAsyncDisposable where TContext : DbContext
{
    /// <summary>
    /// The substituted <see cref="IMediator"/> that a context made by <see cref="CreateContext()"/>
    /// resolves when it publishes entity events. Assert against it to verify published events.
    /// </summary>
    IMediator Mediator { get; }

    /// <summary>The name of this session's database, for the harness's own tests.</summary>
    string DatabaseName { get; }

    /// <summary>
    /// This session's database as a connection string, for handing to configuration rather than to a
    /// context (a per-class factory that boots the application against it).
    /// </summary>
    string ConnectionString { get; }

    /// <summary>
    /// Creates a new context over this session's database. Call more than once when a test needs to
    /// re-read through a cold change tracker. The caller owns it.
    /// </summary>
    TContext CreateContext();

    /// <summary>
    /// Creates a context whose <c>ServiceProvider</c> is <paramref name="services"/>, so that entity
    /// events resolve the mediator out of it. This is how a request gets a context: the factory passes
    /// the request scope, so events reach the application's real handlers rather than
    /// <see cref="Mediator"/>.
    /// </summary>
    TContext CreateContext(IServiceProvider services);
}
