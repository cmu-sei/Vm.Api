// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api adds NewLoggingContext(), for the usage-log database every session also carries.

using Player.Vm.Api.Data;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// Base class for tests that need a database. Each test gets its own isolated database from the shared
/// <see cref="DatabaseFixture"/>.
/// </summary>
/// <remarks>
/// <para>
/// Everything a test uses (<c>Db</c>, <c>NewContext()</c>, <c>Session</c>, <c>Mediator</c>, <c>Ct</c>,
/// <c>Seed</c>, <c>WaitUntil</c>) comes from the shared <see cref="DatabaseTestBase{TContext}"/>.
/// </para>
/// <para>
/// The fixture arrives by constructor injection, which xUnit v3 satisfies from the
/// <c>[assembly: AssemblyFixture(typeof(DatabaseFixture))]</c> declaration in <c>AssemblyFixtures.cs</c>.
/// Derived classes forward it: <c>MyTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)</c>.
/// </para>
/// </remarks>
public abstract class DatabaseTestBase(DatabaseFixture fixture) : DatabaseTestBase<VmContext>(fixture)
{
    /// <summary>The run-wide fixture.</summary>
    protected DatabaseFixture Fixture { get; } = fixture;

    /// <summary>
    /// A context over this test's usage-log database, the second database every session carries (see
    /// <see cref="DatabaseFixture"/>). The caller owns it, as with <c>NewContext()</c>.
    /// </summary>
    protected VmLoggingContext NewLoggingContext() => Session.CreateLoggingContext();
}
