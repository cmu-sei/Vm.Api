// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api keeps no users, roles or permission rows: every permission decision is a question to player.api
// through IPlayerService, which VmAppFactory substitutes. So the actor is the id and name a request
// carries (the sub and name claims the shared TestAuthHandler mints), there are no rows to seed, and its
// permissions are what the substituted player.api answers: Holding(...) makes Factory.PlayerApi answer
// from exactly those grants (PlayerPermissions), which is how a near miss is seeded. The substitute is
// one per host, not per user, so the grants answer for every caller of the test that seeds them.

using System;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Services;

namespace Player.Vm.Api.Tests.Support;

/// <summary>A caller: the identity a request carries.</summary>
public sealed class TestActor
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Sent as the <c>name</c> claim, which vm.api reads into the usage log and the hub presence
    /// (<c>ClaimsPrincipal.GetName</c>).
    /// </summary>
    public required string Name { get; init; }
}

/// <summary>
/// Describes a caller. <see cref="SeedAsync"/> writes no rows: the users and their permissions live in
/// player.api, which the factory substitutes; it arranges the substitute instead.
/// </summary>
public sealed class TestActorBuilder(IPlayerService player, CancellationToken ct)
{
    private Guid _id = Guid.NewGuid();
    private string _name = "Test Actor";
    private PlayerPermissions _permissions;

    /// <summary>Fixes the actor's id, for a test that needs to know it before the request.</summary>
    public TestActorBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public TestActorBuilder WithName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _name = name;
        return this;
    }

    /// <summary>
    /// Exactly these grants, answered by the substituted player.api from <see cref="SeedAsync"/> on, for
    /// every permission question (see <see cref="PlayerPermissions"/>): the near miss of a denied test.
    /// </summary>
    public TestActorBuilder Holding(PlayerPermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (_permissions is not null)
        {
            throw new InvalidOperationException("Holding was called twice; put every grant in one PlayerPermissions.");
        }

        _permissions = permissions;
        return this;
    }

    /// <summary>The actor, with its grants arranged on the substituted player.api.</summary>
    public Task<TestActor> SeedAsync()
    {
        ct.ThrowIfCancellationRequested();

        _permissions?.AnswerFor(player);

        return Task.FromResult(new TestActor { Id = _id, Name = _name });
    }
}
