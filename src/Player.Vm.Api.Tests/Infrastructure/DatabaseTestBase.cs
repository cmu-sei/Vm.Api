// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Data;
using Xunit;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// Base class for tests that need a database. Each test gets its own, cloned from the shared
/// <see cref="DatabaseFixture"/>'s migrated template, so nothing a test writes is visible to any other
/// test and assertions can be made about whole tables rather than only about rows the test seeded.
/// </summary>
/// <remarks>
/// The fixture arrives by constructor injection, which xUnit v3 satisfies from the
/// <c>[assembly: AssemblyFixture(typeof(DatabaseFixture))]</c> declaration in
/// <c>AssemblyFixtures.cs</c>. Derived classes forward it:
/// <c>MyTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)</c>.
/// </remarks>
public abstract class DatabaseTestBase(DatabaseFixture fixture) : IAsyncLifetime
{
    protected DatabaseFixture Fixture { get; } = fixture;

    /// <summary>
    /// The test's own database. Protected because <see cref="ApiTestBase"/> registers it with
    /// <see cref="TestDatabaseScope"/>, which is how a request reaches it.
    /// </summary>
    protected TestDatabaseSession Session { get; private set; }

    /// <summary>
    /// The running test's cancellation token. Passing it to awaited calls is what lets the runner
    /// cancel a test that hangs, which matters more here than in a pure unit test: a query blocked on a
    /// PostgreSQL lock would otherwise hold the whole run open.
    /// </summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The context under test, over a database no other test can see.
    /// </summary>
    protected VmContext Db { get; private set; }

    /// <summary>
    /// The substituted mediator that <c>VmContext.PublishEventsAsync</c> resolves for
    /// <see cref="Db"/> and <see cref="NewContext"/>. Assert on it to verify published entity events.
    /// </summary>
    /// <remarks>
    /// Requests do not publish to this one. <see cref="VmApiFactory"/> hands the request scope to the
    /// context it creates, so an entity event raised by a request reaches the application's real
    /// handlers - which is the point of driving the API over HTTP.
    /// </remarks>
    protected IMediator Mediator => Session.Mediator;

    /// <summary>
    /// An additional context over the same database, for re-reading through a cold change tracker after
    /// a save.
    /// </summary>
    /// <remarks>
    /// The caller owns it: scope it with <c>await using</c>. An undisposed context keeps its pooled
    /// connection checked out for the rest of the run, and one PostgreSQL server serves the whole suite.
    /// </remarks>
    protected VmContext NewContext() => Session.CreateContext();

    /// <summary>
    /// A context over this test's usage log database, which is a second database with its own migration
    /// history - see <see cref="DatabaseFixture"/> for why production keeps them apart.
    /// </summary>
    /// <remarks>
    /// The caller owns it, for the same reason <see cref="NewContext"/>'s caller does. Not created for
    /// every test because almost no test needs it: only the usage log feature reads or writes there.
    /// </remarks>
    protected VmLoggingContext NewLoggingContext() => Session.CreateLoggingContext();

    /// <summary>
    /// Adds entities and saves. Returns nothing, so a test keeps using the references it already holds.
    /// </summary>
    protected async Task Seed(params object[] entities)
    {
        Db.AddRange(entities);
        await Db.SaveChangesAsync(Ct);
    }

    public virtual async ValueTask InitializeAsync()
    {
        Session = await Fixture.BeginSessionAsync();
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
