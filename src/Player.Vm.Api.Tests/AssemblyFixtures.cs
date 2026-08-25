// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

// Starting a PostgreSQL container and running 30 migrations costs seconds, so it happens once for the
// whole assembly. xUnit v3 constructs this before the first test, awaits its InitializeAsync, disposes
// it after the last test, and injects it into any test class - or class fixture - with a matching
// constructor parameter. See DatabaseTestBase, which takes it, and VmApiFactory, which is a class
// fixture that takes it.
//
// VmApiFactory is deliberately *not* declared here. It exposes NSubstitute doubles that tests both
// arrange and assert on, which cannot be shared across test classes running in parallel; the reasoning
// is in its own remarks.
[assembly: AssemblyFixture(typeof(DatabaseFixture))]
