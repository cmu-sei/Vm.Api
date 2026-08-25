// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// Base class for tests that drive the application over HTTP: the real routes, the real middleware, the
/// real authorization policy, the real handlers, over a database no other test can see.
/// </summary>
/// <remarks>
/// <para>
/// One host serves the test class (<see cref="VmApiFactory"/>) and each test owns one database
/// (<see cref="DatabaseTestBase.Session"/>). The two are joined by a session id this class registers
/// with <see cref="TestDatabaseScope"/> and puts on every request its clients send.
/// </para>
/// <para>
/// A request runs in its own scope with its own <c>VmContext</c>, so what a test reads through
/// <see cref="DatabaseTestBase.Db"/> after acting comes from a change tracker that never saw the write.
/// Re-read through <see cref="DatabaseTestBase.NewContext"/> when asserting on what was stored.
/// </para>
/// <para>
/// The database fixture arrives from <c>AssemblyFixtures.cs</c> and the factory from the derived
/// class's <c>IClassFixture&lt;VmApiFactory&gt;</c>. Derived classes forward both:
/// <c>MyTests(DatabaseFixture fixture, VmApiFactory factory) : ApiTestBase(fixture, factory)</c>.
/// </para>
/// </remarks>
public abstract class ApiTestBase(DatabaseFixture fixture, VmApiFactory factory)
    : DatabaseTestBase(fixture)
{
    /// <summary>
    /// Case-insensitive because the API serializes camelCase and the response types these tests
    /// deserialize into are the application's own PascalCase records.
    /// </summary>
    protected static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly List<HttpClient> _clients = [];

    protected VmApiFactory Factory { get; } = factory;

    /// <summary>
    /// A client whose requests authenticate as <see cref="VmApiFactory.UserId"/> and reach this test's
    /// database.
    /// </summary>
    protected HttpClient Client { get; private set; }

    /// <summary>
    /// A client carrying no identity, whose requests to an <c>/api/</c> route are answered with 401.
    /// Still routed to this test's database, so a test can tell a 401 from a routing failure.
    /// </summary>
    protected HttpClient AnonymousClient { get; private set; }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Before any client is used: a request that arrives before its session is registered fails
        // naming the header it could not route.
        TestDatabaseScope.Register(_sessionId, Session);

        Client = Track(Factory.CreateAuthenticatedClient());
        AnonymousClient = Track(Factory.CreateClient());
    }

    public override async ValueTask DisposeAsync()
    {
        // Released first: a request that outlives its test then fails naming the header it could not
        // route, rather than reaching a database being dropped underneath it.
        TestDatabaseScope.Release(_sessionId);

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await base.DisposeAsync();
    }

    private HttpClient Track(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(TestDatabaseScope.HeaderName, _sessionId.ToString());
        _clients.Add(client);

        return client;
    }
}
