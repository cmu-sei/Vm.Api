Player.Vm.Api has an automated test suite in the `src/Player.Vm.Api.Tests` project. This document
details how the suite is built, how to run it, the conventions to follow when adding to it, and -
because the suite is deliberately being grown in stages - what it does not cover yet.

# Testing

The suite contains 640 tests across 24 test classes. All of them run today; nothing is skipped.

It is built on xUnit v3, NSubstitute and Testcontainers, and needs nothing from the environment except
Docker: no network, no vCenter and no Proxmox cluster. The 314 unit tests need not even that.

Fourteen of the twenty-four classes are isolated unit tests. They construct the thing under test
directly and substitute its collaborators. `VsphereIsoProviderTests` and `VsphereServiceCommandTests`
are the largest and most important of them: they drive `VsphereService` and its ISO provider through a
substituted `IVimClient`, which is the only seam between those and a live vCenter.

Seven classes host the application in process and send real HTTP requests through it. Everything between
the request and the hypervisor client is production wiring - routing, model binding, the authorization
policy, the MediatR pipeline behaviors, the handlers, AutoMapper and EF Core against real PostgreSQL -
and only the edges are replaced.

- `VmsEndpointTests` and `BulkPowerOperationEndpointTests` cover `VmController`, the largest surface in
  the application. The bulk power operations are split out because their subject is one contract rather
  than a set of actions: a multi-select power command reports per-VM outcomes, and what those tests pin
  is that the per-VM error survives the handler, the serializer and the wire rather than collapsing into
  one failure for the whole selection.
- `NetworksEndpointTests` covers `NetworksController`, and is the one place the wire format itself is
  asserted against raw JSON rather than through the application's own serializer options.
- `FilesEndpointTests` covers the ISO endpoints. The ISO service is already covered thoroughly by unit
  tests, so this class deliberately does not restate the permission matrix or the listing merge: what
  only a real request reaches is `FileController.Upload`, which reads a multipart form by hand instead
  of binding a model, so its three rejections, the size ceiling and the team-id parsing have no other
  caller.
- `CallbacksEndpointTests` covers the webhook endpoint, the one route whose caller is another service
  rather than a user, and the only one behind the privileged authorization policy.
- `HealthEndpointTests` covers the liveliness and readiness routes. What matters there is not a model or
  a permission but which checks each route runs, since the split between them lives entirely in the
  "live" and "ready" tags on the registrations in `Startup` and nothing else asserts it.
- `ProxmoxEndpointTests` covers `ProxmoxController`. Its seventeen routes all acquire their VM through
  one method, `BaseHandler.GetVm`, which applies the Proxmox-provider guard, the access rules and then
  whatever permission that particular route needs - so the cross-cutting questions are asked as theories
  over the whole route table, and the table itself has a reflection test keeping it in step with the
  controller. Beyond that the class sticks to what only a real request reaches: the values a handler
  hands the cluster, the ISO mount authorization refusing to pass a client's volume id through, the
  view-network rows reaching the NIC options, and which routes wake the task poller. `ProxmoxService`
  itself is substituted, and the ISO and network rules already have unit classes, so neither is restated
  here.

Three classes cover authorization, which is where a green run is easiest to mistake for a safe one:
every other test in the suite runs as a caller who is allowed to do everything, so these are the only
place a refusal is ever observed.

- `PlayerServiceAuthorizationTests` drives `PlayerService` against a substituted `IPlayerApiClient`.
  This is where every authorization decision in the application ends up and nothing above it re-checks
  the verdict, so it is the highest-value class in the suite. It covers how a claim from player.api
  becomes a yes or a no - including the distinction between a permission held directly and one that is
  merely effective, which is what stops a permission scoped onto one team from authorizing another.
- `VmServiceAuthorizationTests` covers which VMs a caller may see and touch. The gates are the simple
  half; the half worth the tests is the filtering, because `GetByViewIdAsync` and `GetByTeamIdAsync`
  answer a caller who should not see a personal VM with an extra list entry rather than an error.
- `NetworkServiceTests` covers the read and write gates over a view's networks and, more importantly,
  `GetEffectiveNetworkPermissions` - the only thing that stops one team attaching a NIC to another
  team's network, and so the only thing that makes a range with several teams in it separable at all.

The two service classes talk to real PostgreSQL, because some of the filtering they are asked about
happens in SQL, but they construct the service directly rather than going over HTTP.

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
never touched what production uses. Without Docker the 326 database tests fail, each naming the reason;
the other 314 still pass, because the container is started by the first test that asks for a database
rather than at assembly load.

A single class or a single test can be run with a filter:

```bash
dotnet test --filter "FullyQualifiedName~VsphereServiceCommandTests"
dotnet test --filter "FullyQualifiedName~VsphereServiceCommandTests.PowerOn_WhenAlreadyOn_ReportsItAndSendsNothing"
```

A full run takes about eleven seconds, container start and migrations included.

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

- **Substituted**: `IVsphereService` and `IProxmoxService` (the hypervisors), `IPlayerService`,
  `IViewService` and `IPlayerApiClient` (player.api, reached over HTTP for authorization decisions and
  for the View a SignalR notification is addressed to), `ITaskService` / `IProxmoxTaskService` (the
  pollers the `CheckTasks` pipeline behaviors poke after a power command), `ICallbackBackgroundService`
  (the webhook send queue) and `IIsoProvider` (ISO storage on a hypervisor).
- **Removed**: every `IHostedService`. The background pollers would otherwise start dialing a vCenter
  that is not there, on their own schedule, in the middle of unrelated tests.
- **Real**: everything else, including `VmService.CanAccessVm` and the handlers' own permission gates.
  `IPlayerService` is the only authorization substitute.

Two of those are worth knowing the reason for, because neither is about avoiding the network:

- `ICallbackBackgroundService` is substituted because the real one is a `BackgroundService` that builds
  its `ActionBlock` in its **constructor**. Removing the `IHostedService` registrations does not stop it:
  handing it an event still starts processing, on a thread pool thread, outside any request - so it
  resolves the host's own `VmContext` and races the test asserting on the row it just wrote.
- `IIsoProvider` replaces only the storage. `IsoService` itself is left real, because the scope
  resolution, the permission gates, the filename sanitizing and the cross-provider merge are the parts
  worth running. The real pair of providers is removed rather than substituted one for one, since
  `IsoService` takes providers as a set: what a test needs to say is "one hypervisor stores ISOs, and
  here is what it holds", not which two happen to be registered. The substitute starts *disabled*, which
  is what an install with no ISO storage configured looks like and is what a test with nothing to do with
  ISOs should see; `EnableIsoProvider()` turns it into a plausible hypervisor.

`AllowEverything()` grants the substituted player.api every permission the endpoints gate on, and a test
that cares about a denial re-stubs the one call it is denying. It is deliberately not exhaustive over
`IPlayerService`: the visibility calls are left unstubbed, because a default that made every team visible
would hide the difference between holding a permission and a team's membership of a View.

Two configuration values the factory writes are asserted on rather than merely set, and both are
exposed as constants for that reason. `MaxIsoFileSize` is at once the ISO size ceiling `UploadIso`
enforces and the multipart body limit `Startup` hands Kestrel, so it is small enough that a test can
exceed it without moving gigabytes. `ProxmoxHost` is not only an address: it is the provider instance id
every Proxmox `ViewNetwork` row is keyed on, so a test seeding one has to agree with it. It is
deliberately not the empty string `appsettings.json` ships, which is also `ProviderInstanceId`'s default
and would let a row belonging to no cluster in particular match. Nothing dials either, since
`IProxmoxService` is substituted and no `PveClient` is ever built.

`Infrastructure/TestMapper.cs` is the application's real AutoMapper configuration, for the tests that
construct a service directly instead of going over HTTP. It is built the way `Startup` builds it -
`AddAutoMapper(typeof(Startup))` over the whole assembly - rather than by registering the profiles a
test happens to need, so a broken profile or a resolver whose dependency goes unregistered fails in
those tests too. `ConsoleUrlOptions` has to be registered alongside it because `ConsoleUrlResolver`
takes it as a constructor dependency, and its values are deliberately recognizable: a test asserting on
a console URL should be reading it from `TestMapper`, not matching a hostname left in a config file.

`Infrastructure/TestAuthHandler.cs` stands in for the JWT bearer handler so no identity server is
needed. A request carrying `X-Test-User` authenticates as that user; a request without it presents no
credentials, which is what keeps the 401 path testable. The scopes come from the factory rather than
being hardcoded, because `Startup` builds its default authorization policy out of
`Authorization:AuthorizationScope`.

A request carrying `X-Test-Privileged` additionally gets the scope behind
`Authorization:PrivilegedScope`, which `CallbacksController` is the only route gated on. That is a
separate opt-in rather than another entry in the ordinary scope list on purpose: the machine-to-machine
callers are the only ones that hold it, and granting it to every test principal would make the 403 on
`CallbacksController` untestable. `ApiTestBase` exposes all three principals - `Client`,
`AnonymousClient` and `PrivilegedClient`.

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
  Clearing matters more than it looks: a substitute retains both its stubs and its received calls, so a
  `Received()` assertion in a class that did not clear can be satisfied by an earlier test's request.
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
7. For an authorization test, substitute `IPlayerService` and construct the service directly, with
   `TestMapper.Value` for the mapper. Make the substitute model the rule rather than the answer where
   the rule is what is in question - `NetworkServiceTests.Holding` returns true when any one of the
   permissions the call site *asked for* is held, which is what lets one test show that view rights
   allow a read and another show that they do not allow a write. A substitute that returns a flat
   true or false per method cannot tell those apart.
8. Break the production code and check the test fails, then restore it with `git checkout`. A test that
   passes the first time it is run has not been shown to assert anything, and every class here has been
   through it. Two things learned that way are now written into the tests themselves:
   `GetByViewId_OnlyMine_PutsThePrimaryTeamsVmFirst` passed with the ordering it exists to protect
   deleted, because the rows happened to arrive in the right order already - it is now a theory over both
   seeding orders. And `HealthEndpointTests.Health_IsReachableWithoutCredentials` did not notice
   `[AllowAnonymous]` being removed from `HealthController`, because with no global authorization filter
   and no `RequireAuthorization` on `MapControllers`, that attribute is decorative; its `<remarks>` now
   says what the test does and does not catch instead of implying it guards the attribute.

   `TreatWarningsAsErrors` makes a bare `if (false)` un-buildable through CS0162. Three mutations that
   do build: `if (false && cond)` in place of `if (cond)`, since the body stays reachable as far as the
   compiler is concerned; a `var mutate = true;` local with `if (!mutate && cond)`, for a guard whose
   body is a `throw` that CS0162 would otherwise catch; and simply changing an operator or a returned
   value. Aim for a mutation that fails *exactly* the test it should: a mutation that reddens half the
   class has usually broken the arrangement rather than the thing under assertion.

   Where a class asks the same question of many routes, mutate in batches whose expected failures have
   different test *names*, and check both the count and the names. `ProxmoxEndpointTests` was verified
   that way in fifteen runs: disabling all four checks in `BaseHandler.GetVm` at once, for instance,
   should fail exactly 39 cases across exactly five tests, and any other total means a mutation landed
   somewhere it was not aimed.

Bugs and deliberate oddities found while writing a test are characterized, not fixed. The test
asserts the current behaviour and says why it is that way.
`VsphereServiceCommandTests` is the worked example: it pins a contract that reads as sloppy error
handling and is not. The VM UI lets a user multi-select machines and hit power on, so one machine
that is already on - or one whose host is unreachable - must not surface as an error for the whole
selection, which is why the service reports outcomes as opaque strings and swallows some faults on
purpose. Several of those assertions would look wrong to someone reading them as "what good code
does"; the comments are what carry that intent through the next refactor.

Where a test would turn red once a real bug is fixed, say so in `<remarks>`, so that whoever makes
the fix knows the failure is expected. Two tests are there for that reason alone, and are the reason
this convention is worth following rather than skipping the awkward case:

- `HealthEndpointTests.Ready_WhenUnhealthy_StillAnswers200`. These are controller actions, not the
  health check middleware, and `UIResponseWriter.WriteHealthCheckUIResponse` writes the report without
  setting a status code - so an unhealthy readiness check answers **200** with a body saying
  `"Unhealthy"`, and a probe configured on the status code alone never fires. The `<remarks>` says the
  fix is to assert 503 here.
- `FilesEndpointTests.Upload_WithABodyOverTheLimit_IsRefusedByTheFormReaderNotTheHandler`. One
  configured value is both the ISO size ceiling and the multipart body length limit, so a body over the
  limit is refused by the form reader while MVC is still building value providers - and `UploadIso`'s own
  `file.Length` check, the authoritative one, is unreachable. Both answers are 400s, so the assertion is
  on the body, which is the only thing that tells them apart.

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
  `VmContext` is now covered against real PostgreSQL; this second context is not. Note the context *is*
  registered even when the feature is disabled - `Startup` registers it unconditionally in the relational
  branch - but it is pointed at the host's own database and never migrated, and `AddPerTestDatabase`
  does not route it. Covering `VmUsageLoggingSessionController` therefore means migrating a second
  template and routing a second context per test, not just flipping the flag.
- **Authorization at the edges of it.** `PlayerService`, `VmService` and `NetworkService` are driven down
  the refusing path as well as the permitting one, and every endpoint class covering an authenticated
  route asserts its 401 and at least one refusal - a 403, or in `BulkPowerOperationEndpointTests` the
  per-VM `"Unauthorized"` a bulk command reports instead. `ProxmoxController` now has the full map of
  which permission each of its routes asks for, including the three that ask for none. What is still only
  ever permitted is what gates itself rather than delegating to those three services:
  `VmUsageLoggingSession`, `VsphereController` and the two SignalR hubs' group membership.
- **Two of the eight controllers.** 52 of the roughly 82 actions have endpoint tests: `VmController`
  (23), `ProxmoxController` (17), `NetworksController` (5), `FileController` (4), `CallbackController`
  (1) and `HealthCheckController` (2). `VsphereController` (21) and `VmUsageLoggingSessionController`
  (9, including CSV and report generation) have none, and neither do the two SignalR hubs. The AutoMapper
  profiles run in every endpoint test, but only the projections those tests happen to read are asserted.
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
4. Breadth, authorization first, then the endpoint surface by controller. Authorization is done:
   `PlayerService`, `VmService` and `NetworkService` each have a class driving their refusing paths.
   Six of the eight controllers are done - `Vm`, `Proxmox`, `Networks`, `File`, `Callback` and
   `HealthCheck`. `Vsphere` is next; `VmUsageLoggingSession` needs the second `DbContext` routed per
   test first, as described under "What is not covered yet".
5. Coverage measurement, opt-in and ungated, purely as a map of where the untested risk still is.
