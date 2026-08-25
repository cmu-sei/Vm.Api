Player.Vm.Api has an automated test suite in the `src/Player.Vm.Api.Tests` project. This document
details how the suite is built, how to run it, the conventions to follow when adding to it, and -
because the suite is deliberately being grown in stages - what it does not cover yet.

# Testing

The suite contains 305 tests across 15 test classes. All of them run today; nothing is skipped.

It is built on xUnit v3, NSubstitute and Testcontainers, and needs nothing from the environment except
Docker: no network, no vCenter and no Proxmox cluster. The 286 unit tests need not even that.

Thirteen of the fifteen classes are isolated unit tests. They construct the thing under test
directly and substitute its collaborators. `VsphereServiceCommandTests` is the largest and the most
important of them: it drives `VsphereService` through a substituted `IVimClient`, which is the only
seam between that service and a live vCenter.

`BulkPowerOperationEndpointTests` hosts the application in process and sends real HTTP requests
through it. Everything between the request and the hypervisor client is production wiring - routing,
model binding, the authorization policy, the MediatR pipeline behaviors, the handlers, AutoMapper and
EF Core against real PostgreSQL - and only the edges are replaced.

`Infrastructure/DatabaseHarnessTests` tests the harness itself. Each of its assertions guards a
property the rest of the suite silently relies on and which would otherwise degrade without failing
anything: that the provider really is Npgsql, that every migration is applied, that snake_case casing
and store-generated UUIDs reached the schema, that foreign keys are enforced, that a test sees only its
own rows, and that a request writes to the database of the test that made it.

# Running the tests

```bash
dotnet test
```

**Docker must be running.** PostgreSQL is the only database these tests use, and there is deliberately
no in-memory or SQLite fallback - a fallback that quietly swaps the provider reports a green run that
never touched what production uses. Without Docker the 19 database tests fail, each naming the reason;
the other 286 still pass, because the container is started by the first test that asks for a database
rather than at assembly load.

A single class or a single test can be run with a filter:

```bash
dotnet test --filter "FullyQualifiedName~VsphereServiceCommandTests"
dotnet test --filter "FullyQualifiedName~VsphereServiceCommandTests.PowerOn_WhenAlreadyOn_ReportsItAndSendsNothing"
```

A full run takes about seven seconds, container start and migrations included.

There is no coverage collection configured. Nothing gates on a coverage figure, and adding a
collector without a threshold step would produce a number nobody reads.

# Build settings

`Directory.Build.props` sets `TreatWarningsAsErrors` for every project, which is what enforces the
xUnit analyzers that ship with `xunit.v3`. These fail the build:

- xUnit1013 - A public method on a test class with no `[Fact]`.
- xUnit1026 - A `[Theory]` parameter the test does not use.
- xUnit1051 - An awaited call that does not take the test's cancellation token.
- xUnit2000 - `Assert.Equal` with the expected value passed second.
- xUnit2012 - `Assert.True` over a collection lookup, where `Assert.Contains` says what failed.

xUnit1051 is the one that earns its keep here. Without a token, the runner cannot cancel a test left
hanging on the in-process host, so a hung test hangs the whole CI job instead of failing it.

`.editorconfig` raises xUnit1004, so a test parked with `[Fact(Skip = "...")]` fails the build.
Conditional skips are unaffected, because `SkipWhen` and `SkipUnless` do not match that rule. Code
style is deliberately not legislated: the API uses block-scoped namespaces and the test project uses
file-scoped ones, and encoding one of them would turn every file into a diff without making anything
more correct.

Restore-time warnings stay warnings, through `WarningsNotAsErrors`. NU1901 to NU1904 are the NuGet
audit, and it has findings here. `AutoMapper` 13 is the only direct one, and it is pinned on purpose
because version 15+ requires a commercial license; `MediatR` 12 is pinned for the same reason.
Everything else is transitive and cannot be fixed in a project file in this repository:
`KubernetesClient` and the OpenTelemetry packages arrive through `Crucible.Common.ServiceDefaults`,
`Microsoft.OpenApi` through Swashbuckle, `SQLitePCLRaw` through the EF SQLite provider,
`Microsoft.NETCore.App` 2.1.0 through `VimClient`, and `System.Drawing.Common` and
`System.Security.Cryptography.Xml` through the health-check UI packages.

Unlike the compiler warnings, that is a real backlog rather than a set of findings that can never be
acted on. Check where it stands when bumping a dependency:

```bash
dotnet list package --vulnerable --include-transitive
```

Package versions live in `Directory.Packages.props`. Every `PackageReference` in the repository takes
its version from there, and `CentralPackageVersionOverrideEnabled` is false so that a project cannot
pin itself to a different version of a shared package. One entry is versioned against the application
rather than against the test tools around it: `Microsoft.AspNetCore.Mvc.Testing`, which hosts the
application, tracks the ASP.NET Core version, since it has to build the host that the application's
own framework reference expects.

# How the harness works

`Infrastructure/VmApiFactory.cs` is a `WebApplicationFactory<Startup>`. Its own doc comment is the
authoritative description of what is real and what is not; the shape of it is:

- **Substituted**: `IVsphereService` and `IProxmoxService` (the hypervisors), `IPlayerService`
  (player.api, reached over HTTP for authorization decisions), and `ITaskService` /
  `IProxmoxTaskService` (the pollers the `CheckTasks` pipeline behaviors poke after a power command).
- **Removed**: every `IHostedService`. The background pollers would otherwise start dialing a vCenter
  that is not there, on their own schedule, in the middle of unrelated tests.
- **Real**: everything else, including `VmService.CanAccessVm` and the handlers' own permission gates.
  `IPlayerService` is the only authorization substitute.

`Infrastructure/TestAuthHandler.cs` stands in for the JWT bearer handler so no identity server is
needed. A request carrying `X-Test-User` authenticates as that user; a request without it presents no
credentials, which is what keeps the 401 path testable. The scopes come from the factory rather than
being hardcoded, because `Startup` builds its default authorization policy out of
`Authorization:AuthorizationScope`.

## The database

`Infrastructure/DatabaseFixture.cs` owns the database for the whole run. It starts `postgres:16-alpine`
in a container - the same image player.api's suite uses, so the two are not proving things against
different servers - and migrates one **template** database. Each test then gets its own database created
with `CREATE DATABASE ... TEMPLATE`, which is a file-level copy and costs milliseconds where re-running
the 30 migrations costs seconds. It is declared once for the assembly in `AssemblyFixtures.cs`.

Two non-obvious things hold that together, both established by experiment:

- **Nothing may hold a connection to the template.** `CREATE DATABASE ... TEMPLATE` fails while any
  session is connected to it, and disposing an `NpgsqlConnection` returns it to the pool rather than
  closing the socket - so a single `await using` against the template makes every later clone fail. The
  fixture calls `NpgsqlConnection.ClearAllPools()` after migrating, and the hosted application is given
  a throwaway database of its own rather than being pointed at the template.
- **Concurrent clones need no lock.** PostgreSQL serializes concurrent clones of an idle template
  itself; eight simultaneous callers were verified against this template. What breaks cloning is a
  connection, not a concurrent clone.

Isolation is a database per test rather than a rolled-back transaction on purpose. The
`EntityEventInterceptor` publishes entity events on `TransactionCommitted` and discards its tracked
state on `TransactionRolledBack`, so wrapping each test in a transaction would silently stop entity
events from firing.

Tests reach this through `Infrastructure/DatabaseTestBase.cs`, which gives each test `Db` (a context
over its own database), `NewContext()` for re-reading through a cold change tracker, `Seed(...)` and
`Ct`. `Infrastructure/ApiTestBase.cs` adds the HTTP side.

## How a request finds its database

`AddEventPublishingDbContextFactory` pools one set of `DbContextOptions` when the container is built,
with one connection string baked in, so there is no point at which the application's own registration
could choose a database per request. `VmApiFactory` therefore replaces the scoped `VmContext`
registration with one that reads an `X-Test-Session` header and looks the test up in
`Infrastructure/TestDatabaseScope.cs`. `ApiTestBase` sets that header on every client it hands out.

A header rather than an `AsyncLocal`: a lookup that misses fails loudly and names the request it could
not route, where an ambient value that failed to flow across a thread hop would silently resolve some
other test's database.

The one place a context is legitimately resolved with no request in flight is startup.
`Program.Main` matches neither convention `HostFactoryResolver` looks for, so `WebApplicationFactory`
invokes `Main` on a background thread - and `Main` calls `InitializeDatabase`, which resolves a
`VmContext` and calls `Migrate` on it. Nothing gates that off, so the host is given a clone of the
already-migrated template, which makes the migrate a no-op. `DatabaseHarnessTests` asserts that no
request ever lands there.

## Two things a new endpoint test has to respect

- `VmApiFactory` is a **class** fixture, so its substitutes are shared across the class, but the
  database is not - it is per test. Clear the substitutes in `InitializeAsync`;
  `BulkPowerOperationEndpointTests` calls `ClearSubstitute()` on each and then `AllowEverything()`.
- It is deliberately not an assembly fixture. Tests both arrange return values on and assert
  `Received()` against its NSubstitute doubles, and NSubstitute keeps assertion state per thread, so
  one shared set would lose and cross-attribute calls once classes ran in parallel. The cost is about a
  second of host startup per endpoint test class; when that starts to hurt, the answer is hand-written
  session-keyed fakes, not a shared host.

# Adding a test

1. Put the file next to the ones covering the same area. The unit tests are named after the type
   under test (`ProxmoxIsoStorageServiceTests`, `VsphereServiceCommandTests`); the harness lives in
   `Infrastructure/`.
2. Prefer a unit test with substituted collaborators for anything below the HTTP layer. Derive from
   `DatabaseTestBase` when the assertion is about what was stored, and from `ApiTestBase` (plus
   `IClassFixture<VmApiFactory>`) when it is a contract that has to survive the handler, the serializer
   and the wire - a status code, a response body shape, an authorization outcome. Both cost a database;
   a unit test does not.
3. Name the method as a sentence. The failure summary is all a reader of CI output gets.
4. Pass a cancellation token to anything awaited, including inside private helpers - `Ct` on the two
   base classes, `TestContext.Current.CancellationToken` elsewhere. xUnit1051 only sees test methods,
   but a helper that hangs hangs the run just the same.
5. Scope anything from `NewContext()` with `await using`. One PostgreSQL server serves the whole run,
   and an undisposed context keeps its pooled connection checked out until the process exits.
6. Do not add Arrange, Act and Assert comments. A blank line already shows the shape of a test.
   Comments should explain what a test pins and why it matters, not what the next line does.

Bugs and deliberate oddities found while writing a test are characterized, not fixed. The test
asserts the current behaviour and says why it is that way.
`VsphereServiceCommandTests` is the worked example: it pins a contract that reads as sloppy error
handling and is not. The VM UI lets a user multi-select machines and hit power on, so one machine
that is already on - or one whose host is unreachable - must not surface as an error for the whole
selection, which is why the service reports outcomes as opaque strings and swallows some faults on
purpose. Several of those assertions would look wrong to someone reading them as "what good code
does"; the comments are what carry that intent through the next refactor.

Where a test would turn red once a real bug is fixed, say so in `<remarks>`, so that whoever makes
the fix knows the failure is expected.

# Continuous integration

`.github/workflows/test.yml` restores, builds and runs the suite on every push and pull request. It
is not scoped to a branch list, so a regression surfaces on the branch that introduced it rather
than waiting for a pull request to be opened. Publishing is gated separately, in `main.yml`.

The job needs no database service block. `ubuntu-latest` runners ship a working Docker daemon, and
Testcontainers starts and disposes the container itself. Nothing needs to force PostgreSQL on either -
there is no fallback to force it away from, so a CI run cannot go green without having used it.

The job builds the whole solution rather than just the test project. `Player.Vm.Api` would be built
either way, through the test project's `ProjectReference`, but `Player.Vm.Api.Client` would not:
`main.yml` builds the image and `client.yml` is `workflow_dispatch`, so this is the only workflow
that compiles the Client on a push. With `TreatWarningsAsErrors` in force, a warning there would
otherwise first surface at release time.

The run produces one artifact, `test-results`, the TRX of the run. It is uploaded even when the job
fails, so that a failure shows which tests failed rather than only a count, and
`if-no-files-found: error` means a run that produced no TRX at all - a test host that died before
writing one - fails rather than passing quietly. The NuGet cache is keyed on
`Directory.Packages.props` and the project files, which are what decide what a restore pulls.

# What is not covered yet

The suite is being grown in stages, and it is worth being explicit about what a green run does *not*
currently tell you.

- **`VmLoggingContext`.** The VM usage log runs with `VmUsageLogging:Enabled` false, so its own
  migrations, its `if (Database.IsNpgsql())` branch and every handler that writes to it are untested.
  `VmContext` is now covered against real PostgreSQL; this second context is not.
- **Authorization.** `IPlayerService` is substituted and `AllowEverything()` is the default, so
  `VmService.CanAccessVm`, the `includePersonal` and `onlyMine` filtering in `GetByViewIdAsync` and
  `GetByTeamIdAsync`, `GetEffectiveNetworkPermissions` and the scoped team permissions are only ever
  driven down the permitting path.
- **Most of the HTTP surface.** One endpoint class exists, against roughly 82 actions across eight
  controllers. `VmUsageLoggingSession` (including CSV and report generation), `Networks`, the maps and
  coordinates on `VmController`, `Callbacks` and its `WebhookEvents` table, the Proxmox and vSphere
  command and query handlers, both SignalR hubs, the four AutoMapper profiles and the two `CheckTasks`
  pipeline behaviors have no test.
- **The hypervisor edge, permanently.** No harness makes a vCenter or a Proxmox cluster available in
  CI. Unit tests against `IVimClient` and the Proxmox interfaces are the right tool at that layer and
  are not meant to be replaced by anything further up.

## Roadmap

1. ~~Build-level enforcement: `Directory.Build.props`, `.editorconfig`, and a CI job that guards
   every branch.~~ Done.
2. ~~Central package management, so a shared package cannot drift between the application and the
   tests.~~ Done.
3. ~~A real PostgreSQL instance, started per run in a container, with an isolated database per test.~~
   Done. The `Startup` change originally planned alongside it turned out not to be needed: the
   in-memory store name only mattered while tests used the in-memory provider.
4. Breadth, authorization first, then the endpoint surface by controller.
5. Coverage measurement, opt-in and ungated, purely as a map of where the untested risk still is.
