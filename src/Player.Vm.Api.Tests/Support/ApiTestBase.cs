// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api keeps no users or permissions of its own: PlayerService asks player.api, whose client the
// factory substitutes. So an actor is the identity a request carries, and what it may do is the
// player.api data its builder steps write (see TestActor). Each test starts with a clean player.api and
// a fresh Root, which holds every system permission.
// Also here: NewLoggingContext(), for the usage-log database, and JsonOptions, the host's own serializer
// options.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Player.Vm.Api.Data;
using Player.Vm.Api.Infrastructure.Authorization;

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
    /// The caller for the tests that are about what an endpoint does rather than who may call it: a fresh
    /// id per test, holding every system permission. A gate that asks only for a View or team permission
    /// needs Root on that View or team as well (<c>Actor().WithId(Root.Id)...</c> adds to it).
    /// </summary>
    protected TestActor Root { get; private set; }

    /// <summary>A client that acts as <see cref="Root"/>.</summary>
    protected HttpClient RootClient => Client(Root);

    /// <summary>
    /// Starts describing another caller:
    /// <c>await Actor().Holding(AppViewPermission.ViewViewMaps, viewId).SeedAsync()</c>.
    /// </summary>
    protected TestActorBuilder Actor() => new(Factory.Player, Ct);

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

        // Resolving from Factory.Services is what builds the host, if no client has yet.
        JsonOptions = Factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;

        Factory.ResetPlayer();
        Root = await Actor().WithName("Root").Holding(Enum.GetValues<AppSystemPermission>()).SeedAsync();
    }
}
