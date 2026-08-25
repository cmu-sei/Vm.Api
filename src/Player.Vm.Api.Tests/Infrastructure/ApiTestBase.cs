// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    /// The options the application serializes its responses with, taken from the running host rather
    /// than restated here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Restating them means maintaining a second copy: <c>Startup</c> adds a
    /// <c>JsonStringEnumConverter</c>, so an enum goes out as <c>"Vsphere"</c> rather than a number, and a
    /// test deserializing with plain case-insensitive options fails on it for a reason that has nothing
    /// to do with what it was asserting.
    /// </para>
    /// <para>
    /// Note what this deliberately does not do: because these are the application's own options, a test
    /// using them follows the application if the wire format changes. Nothing here would notice the
    /// converter being removed, which would break every generated client. That belongs in an assertion
    /// against the raw JSON - see
    /// <c>NetworksEndpointTests.GetById_SerializesAnEnumAsItsName</c>.
    /// </para>
    /// </remarks>
    protected JsonSerializerOptions JsonOptions { get; private set; }

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

    /// <summary>
    /// A client whose requests additionally carry the scope behind the privileged authorization policy.
    /// Only <c>CallbacksController</c> is gated on it.
    /// </summary>
    protected HttpClient PrivilegedClient { get; private set; }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Before any client is used: a request that arrives before its session is registered fails
        // naming the header it could not route.
        TestDatabaseScope.Register(_sessionId, Session);

        Client = Track(Factory.CreateAuthenticatedClient());
        AnonymousClient = Track(Factory.CreateClient());
        PrivilegedClient = Track(Factory.CreatePrivilegedClient());

        // After the clients, because resolving from Factory.Services is what builds the host.
        JsonOptions = Factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;
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
