// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Crucible.Api.Testing;

/// <summary>
/// The core of the application's <c>DatabaseTestBase</c>: each test gets its own isolated database from
/// the run-wide fixture.
/// </summary>
/// <remarks>
/// The application's <c>DatabaseTestBase(DatabaseFixture fixture)</c> derives from this and adds the
/// <c>Fixture</c> property; tests derive from that one, never from this class directly.
/// </remarks>
public abstract class DatabaseTestBase<TContext>(ITestDatabaseSessionSource<TContext> sessions) : IAsyncLifetime
    where TContext : DbContext
{
    /// <summary>
    /// The test's own database. Protected because the application's <c>ApiTestBase</c> registers it with
    /// <see cref="TestDatabaseScope"/>, which is how a request reaches it.
    /// </summary>
    protected ITestDatabaseSession<TContext> Session { get; private set; }

    /// <summary>
    /// The running test's cancellation token. Passing it to awaited calls is what lets the runner cancel
    /// a test that hangs: a query blocked on a PostgreSQL lock would otherwise hold the run open.
    /// xUnit1051 enforces it in test methods.
    /// </summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The context under test. Created once per test over a database no other test can see.</summary>
    protected TContext Db { get; private set; }

    /// <summary>
    /// The substituted mediator that a context made by the test resolves when it publishes entity events.
    /// Assert on it to verify published events. A context made for a request resolves the request's own
    /// mediator instead, so entity events reach the real handlers there.
    /// </summary>
    protected IMediator Mediator => Session.Mediator;

    /// <summary>
    /// Creates an additional context over the same database, for re-reading through a cold change
    /// tracker after a save.
    /// </summary>
    /// <remarks>
    /// The caller owns it: scope it with <c>await using</c>, or hand it to something that disposes it. An
    /// undisposed context keeps its pooled connection checked out for the rest of the run, and one
    /// PostgreSQL server serves the whole suite.
    /// </remarks>
    protected TContext NewContext() => Session.CreateContext();

    /// <summary>
    /// Runs <paramref name="query"/> on a fresh <see cref="NewContext"/> and disposes it: a cold re-read in
    /// one expression, <c>var stored = await ReadBack(db =&gt; db.Views.SingleAsync(x =&gt; x.Id == id, Ct));</c>.
    /// </summary>
    /// <remarks>
    /// The disposing form of <c>NewContext().Views.SingleAsync(...)</c>, which leaves the context and its
    /// pooled connection checked out for the rest of the run (<c>check-repo.js tests</c> fails it). Use
    /// <c>await using var db = NewContext();</c> where a test reads several things from one context.
    /// </remarks>
    protected async Task<T> ReadBack<T>(Func<TContext, Task<T>> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var context = NewContext();

        return await query(context);
    }

    /// <summary>Adds entities and saves. Returns nothing, so a test keeps using the references it holds.</summary>
    protected async Task Seed(params object[] entities)
    {
        Db.AddRange(entities);
        await Db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, or fails the test naming <paramref name="what"/>.
    /// A last resort; see <see cref="Waits.Until"/>.
    /// </summary>
    protected static Task WaitUntil(Func<Task<bool>> condition, string what) => Waits.Until(condition, what, Ct);

    public virtual async ValueTask InitializeAsync()
    {
        Session = await sessions.BeginSessionAsync();
        Db = NewContext();
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (Db is not null)
        {
            await Db.DisposeAsync();
        }

        if (Session is not null)
        {
            await Session.DisposeAsync();
        }
    }
}
