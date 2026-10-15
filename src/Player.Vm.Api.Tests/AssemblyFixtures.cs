// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api uses the per-class factory (VmAppFactory, through IClassFixture), because its tests arrange and
// assert on the NSubstitute doubles the host resolves, so only the database fixture is declared here.

using Player.Vm.Api.Tests.Support;

// Starting PostgreSQL and running the migrations costs seconds, so it happens once for the whole assembly.
// xUnit v3 constructs this before the first test and injects it into any test class or class fixture with
// a matching constructor parameter. The containers start on the first test that asks for a database (see
// PostgresTestDatabase), so tests that need none run without Docker.
[assembly: AssemblyFixture(typeof(DatabaseFixture))]
