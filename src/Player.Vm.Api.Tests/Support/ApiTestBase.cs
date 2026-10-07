// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api keeps no users or permissions of its own: it asks player.api, which the factory substitutes.
// So an actor is the identity a request carries, and what it may do is what the substituted player.api
// answers: the class's own arrangement for Root, Actor().Holding(...) for a near miss (see TestActor).
// Also here: NewLoggingContext(), for the usage-log database, and JsonOptions, the host's own serializer
// options.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Player.Vm.Api.Data;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// Base class for tests that drive the application over HTTP: the real routes, the real middleware, the
/// real authorization policies, the real handlers, over a database no other test can see.
/// </summary>
/// <remarks>
/// Derived classes forward both fixtures and receive the factory as a class fixture:
/// <c>MyTests(DatabaseFixture fixture, VmAppFactory factory) : ApiTestBase(fixture, factory), IClassFixture&lt;VmAppFactory&gt;</c>.
/// </remarks>
public abstract class ApiTestBase(DatabaseFixture fixture, VmAppFactory factory)
    : ApiTestBase<VmContext>(fixture, factory)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected VmAppFactory Factory { get; } = factory;

    /// <summary>
    /// The caller for the tests that are about what an endpoint does rather than who may call it: the
    /// identity <see cref="VmAppFactory.UserId"/>, holding whatever the class arranged on
    /// <see cref="VmAppFactory.PlayerApi"/> (every permission, in a class that calls
    /// <see cref="VmAppFactory.AllowEverything"/>).
    /// </summary>
    protected TestActor Root { get; private set; }

    /// <summary>A client that acts as <see cref="Root"/>.</summary>
    protected HttpClient RootClient => Client(Root);

    /// <summary>
    /// Starts describing another caller:
    /// <c>await Actor().Holding(new PlayerPermissions().Holding(AppViewPermission.ViewViewMaps)).SeedAsync()</c>.
    /// </summary>
    protected TestActorBuilder Actor() => new(Factory.PlayerApi, Ct);

    /// <summary>A client that acts as <paramref name="actor"/>, cached per actor.</summary>
    protected HttpClient Client(TestActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return ClientFor(actor.Id, actor.Name);
    }

    /// <summary>
    /// The options the application serializes its responses with, taken from the running host rather
    /// than restated here: <c>Startup</c> adds a <c>JsonStringEnumConverter</c>, so an enum goes out as
    /// its name. A test about the wire format itself asserts on the raw JSON instead, because these
    /// options follow the application wherever it goes.
    /// </summary>
    protected JsonSerializerOptions JsonOptions { get; private set; }

    /// <summary>
    /// A context over this test's usage-log database, the second database every session carries (see
    /// <see cref="DatabaseFixture"/>). The caller owns it, as with <c>NewContext()</c>.
    /// </summary>
    protected VmLoggingContext NewLoggingContext() => Session.CreateLoggingContext();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        Root = await Actor().WithId(Factory.UserId).WithName("Root").SeedAsync();

        // Resolving from Factory.Services is what builds the host, if no client has yet.
        JsonOptions = Factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;
    }
}
