Player.Vm.Api has an automated test suite in the `src/Player.Vm.Api.Tests` project. This document
details how the suite is built, how to run it, the conventions to follow when adding to it, and -
because the suite is deliberately being grown in stages - what it does not cover yet.

# Testing

The suite contains 992 tests across 32 test classes. All of them run today; nothing is skipped.

It is built on xUnit v3, NSubstitute and Testcontainers, and needs nothing from the environment except
Docker: no network, no vCenter and no Proxmox cluster. The 321 unit tests need not even that.

Fifteen of the thirty-two classes are isolated unit tests. They construct the thing under test
directly and substitute its collaborators. `VsphereIsoProviderTests` and `VsphereServiceCommandTests`
are the largest and most important of them: they drive `VsphereService` and its ISO provider through a
substituted `IVimClient`, which is the only seam between those and a live vCenter.

Twelve classes host the application in process and send real HTTP requests through it. Everything between
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
- `VsphereEndpointTests` covers `VsphereController`, and is built the same way: a table of its
  twenty-one routes with a reflection test keeping it in step with the controller, theories over the
  whole table for the questions every route shares, and then only what a real request reaches. Two
  things make it differ from the Proxmox class rather than mirror it. There is no provider guard, so a
  Proxmox VM can be addressed through any vSphere route and nothing refuses it - characterized in one
  test that says why, since the real `VsphereService` keys off its own connection cache and fails
  inside the service instead. And the provider instance id a view-network row must agree with is not
  configuration here but `IVsphereService.GetConnectionAddress`, so which vCenter a VM sits on is
  something a test decides per VM; a portgroup registered against another vCenter reaches neither the
  NIC options nor a network change.
- `VmUsageLoggingSessionEndpointTests` covers `VmUsageLoggingSessionController` on a host with
  `VmUsageLogging:Enabled` true. It is the only endpoint class that touches the second database, and one
  of two classes whose subject is a feature switched off in the shipped configuration - the other being
  `VmUsageLoggingServiceTests`, which covers the writer behind it. Its nine routes get the same
  treatment as Proxmox's and vSphere's - a route table with a reflection test behind it, theories over
  the whole table - plus a table of which permission each route asks for, driven twice: once denying that
  permission and once denying the opposite one, so a handler asking for the wrong pair cannot pass. The
  rest is the two things only a request reaches: the CSV the download builds by hand out of string
  concatenation and ASCII bytes, and the report's grouping and minute arithmetic.
- `VmUsageLoggingDisabledEndpointTests` covers the other branch of that controller's
  `if (_options.Enabled)`, on the plain factory - which makes it also the assurance that the eight
  classes above are running against the configuration `appsettings.json` ships. Its table is tied by a
  count to the other class's, so that between them the two still account for every action and a route
  added to the controller cannot go untested on one side of the flag.
- `HubConnectionTests` covers the edge of the two SignalR hubs - where they are mapped, who may reach
  them, and one round trip over a real connection. It is described with the other hub classes below.

Three classes cover authorization, which is where a green run is easiest to mistake for a safe one:
every other test in the suite runs as a caller who is allowed to do everything unless it says otherwise,
so these are the only place the decision itself is under test rather than a route's use of it.

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
happens in SQL, but they construct the service directly rather than going over HTTP. Three of the classes
below - `VmHubGroupTests`, `VmHubPresenceTests` and `VmUsageLoggingServiceTests` - are built the same way,
which makes five in the suite that need a database without needing a host.

Four classes cover the two SignalR hubs, which carry everything the application pushes rather than
answers: console progress while a VM boots, and who else is looking at a machine. A hub is not reachable
the way a controller is - there is no route to assert and no status code to read - so what is under test
is the **group name**, since a name is the whole of the addressing. A hub that joins the wrong group and
one that joins the right one are indistinguishable from either side until a broadcast arrives, and the
other side of every one of these names is a `Clients.Group(...)` somewhere else in the application: in
the vSphere and Proxmox task pollers for `ProgressHub`, in the entity-event handlers
(`VmUpdatedSignalRHandler` and `VmTeamUpdatedSignalRHandler`) for `VmHub`, and in the Angular client's own
subscriptions for both.

- `ProgressHubTests` covers `ProgressHub`, which is two methods and no dependencies, so it is a unit
  test with no database. What it pins is that `Join` uses the string it was given verbatim - no
  normalization, no casing fold - because the pollers broadcast to `vmId.ToString()` and any
  transformation on the joining side silently drops the messages. It also characterizes the fact that
  `Join` authorizes nothing: any authenticated caller can subscribe to any Vm's progress by guessing an
  id.
- `VmHubGroupTests` covers the four join and leave pairs on `VmHub` - view, view users, user and vm -
  which resolve the caller's visibility out of the database and so derive from `DatabaseTestBase`. Every
  leave is driven against the join it undoes rather than against a literal, since a leave that removes a
  name nothing joined is indistinguishable from a working one. Two of its findings are characterizations
  rather than assertions of intent, and are described below:
  `LeaveView_WhenVisibilityNarrowed_LeavesOnlyWhatIsStillVisible` and
  `JoinVm_ForATeamMember_AlsoSubscribesTeamsTheVmIsNotOn`.
- `VmHubPresenceTests` covers the other half of `VmHub`: `SetActiveVirtualMachine`,
  `UnsetActiveVirtualMachine` and `OnDisconnectedAsync`, which are how the UI knows who is on a console.
  It is the only class that drives the real `ActiveVirtualMachineService`, and the place the calls into
  the usage log are asserted - the service itself is substituted there, because what the hub owes it is
  the user, the Vm and the teams, not a row. The two that matter most are the pairing of set and unset -
  unset publishes to the teams recorded when the console was opened, not to the ones visible now - and
  `OnDisconnected_FromAnotherConnectionOfTheSameUser_ClearsNothing`, since presence is keyed per
  connection and a second browser tab must not clear the first one's.
- `HubConnectionTests` is the part none of those three can reach, and is the one hub class that hosts the
  application. A real client cannot see a group name; what it can see, and they cannot, is that the hub
  is mapped at the path the Angular client dials, that every endpoint a `MapHub` produces requires
  authorization rather than only the negotiate route, that nothing else is mapped as a hub - including
  `Player.Vm.Api.Hubs.VmHub`, a second unmapped copy of the type that would compile and pass everything
  if edited by mistake - and that a broadcast addressed to a joined name actually arrives while one
  addressed elsewhere does not. Only `ProgressHub` is driven over the connection: a hub invocation is not
  an HTTP request, so it cannot carry `X-Test-Session`, and a `VmHub` method invoked over a connection
  would resolve the *host's* database rather than the test's.

`VmUsageLoggingServiceTests` covers `VmUsageLoggingService`, the writer behind that log, directly
against the usage log database - which sessions a console visit is logged against, what a row says, when
a visit is closed, and the `DisabledVmUsageLoggingService` that stands in for it when the feature is off.
It is separated from the hub that calls it because the rules it applies are its own: a session window,
the intersection between the caller's teams and the session's, and a close filter with three clauses. It
is also the only place a usage log row is written by the application rather than seeded by a test.

`Infrastructure/DatabaseHarnessTests` tests the harness itself. Each of its assertions guards a
property the rest of the suite silently relies on and which would otherwise degrade without failing
anything: that the provider really is Npgsql, that every migration is applied, that snake_case casing
and store-generated UUIDs reached the schema, that foreign keys are enforced, that a test sees only its
own rows, that a request writes to the database of the test that made it, and that the usage log is a
second database of its own rather than the same one with more tables in it.

# Running the tests

```bash
dotnet test
```

**Docker must be running.** PostgreSQL is the only database these tests use, and there is deliberately
no in-memory or SQLite fallback - a fallback that quietly swaps the provider reports a green run that
never touched what production uses. Without Docker the 671 database tests fail, each naming the reason;
the other 321 still pass, because the container is started by the first test that asks for a database
rather than at assembly load.

A single class or a single test can be run with a filter:

```bash
dotnet test --filter "FullyQualifiedName~VsphereServiceCommandTests"
dotnet test --filter "FullyQualifiedName~VsphereServiceCommandTests.PowerOn_WhenAlreadyOn_ReportsItAndSendsNothing"
```

A full run takes about fifteen seconds, container start and both sets of migrations included.

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
pin itself to a different version of a shared package. Two entries are versioned against the application
rather than against the test tools around them, because both have to agree with the framework the
application is built on rather than with each other: `Microsoft.AspNetCore.Mvc.Testing`, which has to
build the host the application's own framework reference expects, and
`Microsoft.AspNetCore.SignalR.Client`, which has to speak the protocol the hosted server negotiates.
Both track the ASP.NET Core version.

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

`Infrastructure/HubHarness.cs` is what lets a hub method be called at all outside a connection. A `Hub`
reads its caller from `Context`, `Clients` and `Groups`, all three of which SignalR sets after
constructing it, so the harness builds a `HubCallerContext` with a connection id and a `sub` claim and
attaches them with `Attach(hub)`. It then records what the hub did: `Added` and `Removed` are the group
names in order, `AddedChanges` pairs each name with the connection id it was added for, and `Sends`
holds the groups, method name and arguments of every broadcast. The two collaborators are faked
differently on purpose. `IGroupManager` is hand-written, because the group names *are* the subject - a
test wants the whole ordered set, and recording the connection id is what shows a hub adding its own
connection rather than someone else's. `IHubCallerClients` is substituted, because it has a dozen members
that shift between framework versions and only `Group` and `Groups` are ever reached.

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
different servers - and migrates two **template** databases. Each test then gets a pair of its own,
created with `CREATE DATABASE ... TEMPLATE`, which is a file-level copy and costs milliseconds where
re-running the migrations costs seconds. It is declared once for the assembly in `AssemblyFixtures.cs`.

Two templates because production has two databases. `VmContext` reads
`ConnectionStrings:PostgreSQL`; `VmLoggingContext` reads `VmUsageLogging:PostgreSql`, a separate
connection string with its own migration history, and in a real deployment as often as not a separate
server. Pointing both at one database would let a usage log test pass against a schema production does
not have, which is why `DatabaseHarnessTests` asserts the separation from both ends rather than trusting
it. The container creates only the database it was built with, so the second template is created
explicitly before it is migrated.

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
`Ct`. `NewLoggingContext()` is the usage log's equivalent, and is deliberately not a `Db` of its own:
almost no test needs it, and creating one per test would cost every test a connection to a database it
never reads. `Infrastructure/ApiTestBase.cs` adds the HTTP side.

## How a request finds its database

`AddEventPublishingDbContextFactory` pools one set of `DbContextOptions` when the container is built,
with one connection string baked in, so there is no point at which the application's own registration
could choose a database per request. `VmApiFactory` therefore replaces the scoped `VmContext`
registration with one that reads an `X-Test-Session` header and looks the test up in
`Infrastructure/TestDatabaseScope.cs`. `ApiTestBase` sets that header on every client it hands out.
`VmLoggingContext` is replaced the same way and for the same reason - `AddDbContextPool` bakes in one
connection string too - and both resolve through one `SessionFor` helper, so a request cannot end up
reading one test's application database and another's usage log.

A header rather than an `AsyncLocal`: a lookup that misses fails loudly and names the request it could
not route, where an ambient value that failed to flow across a thread hop would silently resolve some
other test's database.

The limit of that mechanism is the hubs. A hub invocation is not an HTTP request - the headers belong to
the connection, and the invocation arrives over it - so a hub method resolving a `VmContext` gets the
host's own throwaway database rather than the calling test's. That is why `VmHub` is driven by direct
invocation over the test's own context and only `ProgressHub`, which touches no database, is driven over
a live connection in `HubConnectionTests`. Making a hub routable would mean keying `TestDatabaseScope` on
the connection id at negotiation time, which is worth doing only if `VmHub` ever needs to be tested from
the client's side.

The one place a context is legitimately resolved with no request in flight is startup.
`Program.Main` matches neither convention `HostFactoryResolver` looks for, so `WebApplicationFactory`
invokes `Main` on a background thread - and `Main` calls `InitializeDatabase`, which resolves a
`VmContext` and calls `Migrate` on it. Nothing gates that off, so the host is given a clone of the
already-migrated template, which makes the migrate a no-op. `DatabaseHarnessTests` asserts that no
request ever lands there. The host gets a throwaway usage log database as well, because
`InitializeDatabase` migrates that one too when the feature is enabled.

## Three things a new endpoint test has to respect

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
- A setting the application reads at **startup** cannot be flipped by a test, and needs a subclass of
  the factory instead. `VmUsageLoggingEnabledFactory` is the worked example, and one boolean is enough to
  need it: `Startup.ConfigureServices` reads `VmUsageLogging:Enabled` once to choose which
  `IVmUsageLoggingService` to register, and `VmUsageLoggingSessionController` captures
  `IOptionsMonitor.CurrentValue` in its constructor rather than per request. Rewriting configuration
  mid-run would leave the first of those stale and prove nothing about a real deployment. Cover both
  sides with one class each rather than one host per test, and leave the default as what
  `appsettings.json` ships, so that every other class keeps testing the shipped configuration.

# Adding a test

1. Put the file next to the ones covering the same area. The unit tests are named after the type
   under test (`ProxmoxIsoStorageServiceTests`, `VsphereServiceCommandTests`); the harness lives in
   `Infrastructure/`.
2. Prefer a unit test with substituted collaborators for anything below the HTTP layer. Derive from
   `DatabaseTestBase` when the assertion is about what was stored, and from `ApiTestBase` (plus
   `IClassFixture<VmApiFactory>`) when it is a contract that has to survive the handler, the serializer
   and the wire - a status code, a response body shape, an authorization outcome. Both cost a database;
   a unit test does not. For a hub method, attach `HubHarness` and invoke it directly, whatever base class
   the assertion needs: the group names are what a hub test is about, and only the hub's edge - its
   mapping, its authorization, a round trip - is worth a live connection.
3. Name the method as a sentence. The failure summary is all a reader of CI output gets.
4. Pass a cancellation token to anything awaited, including inside private helpers - `Ct` on the two
   base classes, `TestContext.Current.CancellationToken` elsewhere. xUnit1051 only sees test methods,
   but a helper that hangs hangs the run just the same. A token is not enough where the thing awaited is
   a `TaskCompletionSource` the test itself completes - nothing will cancel it - so bound those with
   `WaitAsync(TimeSpan, Ct)`, as `HubConnectionTests.Arrives` does.
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
   through it. Three things learned that way are now written into the tests themselves:
   `GetByViewId_OnlyMine_PutsThePrimaryTeamsVmFirst` passed with the ordering it exists to protect
   deleted, because the rows happened to arrive in the right order already - it is now a theory over both
   seeding orders. And `HealthEndpointTests.Health_IsReachableWithoutCredentials` did not notice
   `[AllowAnonymous]` being removed from `HealthController`, because with no global authorization filter
   and no `RequireAuthorization` on `MapControllers`, that attribute is decorative; its `<remarks>` now
   says what the test does and does not catch instead of implying it guards the attribute. And the
   `HubConnectionTests` broadcast tests *hung* the run instead of failing it when `ProgressHub.Join` was
   mutated, because a test awaiting a `TaskCompletionSource` nothing will ever complete has nothing to
   time out; they now go through an `Arrives` helper that bounds the wait with
   `WaitAsync(TimeSpan, Ct)`. Any test whose assertion is "a message arrived" needs that, and a mutation
   run is the only thing that finds it.

   `TreatWarningsAsErrors` makes a bare `if (false)` un-buildable through CS0162. Mutations that do
   build: `if (false && cond)` in place of `if (cond)`, since the body stays reachable as far as the
   compiler is concerned; a `var mutate = true;` local with `if (!mutate && cond)`, for a guard whose
   body is a `throw` that CS0162 would otherwise catch; `var mutate = false; if (mutate) { stmt; }` to
   delete a statement; `.Where(x => true)` or `.ThenBy(x => 0)` to neutralize a clause without removing
   it; `x.CompareTo(x)` in place of `request.Something.CompareTo(x)`, which drops half a window predicate
   and still translates to SQL; and simply changing an operator or a returned value. Note also that
   `command.Id = command.Id;` is CS1717, so an assignment has to be guarded out rather than made a
   self-assignment. Aim for a mutation that fails *exactly* the test it should: a mutation that reddens
   half the class has usually broken the arrangement rather than the thing under assertion.

   Where a class asks the same question of many routes, mutate in batches whose expected failures have
   different test *names*, and check both the count and the names. `ProxmoxEndpointTests` was verified
   that way in fifteen runs, `VsphereEndpointTests` in eleven, the two usage log controller classes in
   eight, and the four hub classes together with `VmUsageLoggingServiceTests` in eighteen:
   disabling all four checks in Proxmox's `BaseHandler.GetVm` at once, for instance, should fail exactly
   39 cases across exactly five tests, and any other total means a mutation landed somewhere it was not
   aimed. Predict the count and the names before running, and treat a surplus as a finding: dropping
   vSphere's `PowerOnVm` call reddens one row of the power theory *and* the Proxmox-VM characterization
   test, because that test drives the same route. Mutating the connection id that `ProgressHub.Join` adds
   reddens `HubConnectionTests`' live-connection tests as well as the unit ones it was aimed at, for the
   same kind of reason: the real client never joins the group at all. A surplus that turns out to be
   explainable is still worth the minute it takes to explain, because the other reading of it is a mutation
   that landed in two places.

   Treat a *shortfall* as a finding too - that is where the weak tests are. Two kinds turned up in the
   usage log class, and both are worth checking for when writing one. A test that asserts only that
   something was left alone passes when the request was refused outright: `Edit_LeavesCreatedDtAlone`
   survived an edit handler that never ran, and now asserts the edit landed before asserting what it did
   not touch. And a test that acts on the *first* row it seeded passes against a handler that ignores the
   id it was given: `Get_ReturnsOnlyTheSessionAsked_For` and its siblings now seed the bystander first, so
   `FirstOrDefault(e => e.Id == request.Id)` losing its predicate reddens all five id routes.

   Two kinds of test cannot be reddened by any mutation of production code, and are worth recognizing so
   the search for a mutation is not spent on them. One asserts a zero-iteration boundary -
   `VmUsageLoggingServiceTests.Create_WithNoSessions_WritesNothing` passes for every rule the loop body
   could contain, because the loop does not run. The other asserts that a null object does nothing:
   `TheDisabledService_WritesNothingForAMatchingSession` has no collaborators to break. Both are still
   worth keeping - the first is the guard against a `First()` on an empty match, and the second is what
   would fail if `DisabledVmUsageLoggingService` ever grew a body - but neither is evidence that the class
   around it asserts anything.

Bugs and deliberate oddities found while writing a test are characterized, not fixed. The test
asserts the current behaviour and says why it is that way.
`VsphereServiceCommandTests` is the worked example: it pins a contract that reads as sloppy error
handling and is not. The VM UI lets a user multi-select machines and hit power on, so one machine
that is already on - or one whose host is unreachable - must not surface as an error for the whole
selection, which is why the service reports outcomes as opaque strings and swallows some faults on
purpose. Several of those assertions would look wrong to someone reading them as "what good code
does"; the comments are what carry that intent through the next refactor.

Where a test would turn red once a real bug is fixed, say so in `<remarks>`, so that whoever makes
the fix knows the failure is expected. Several tests are there for that reason alone, and are the reason
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
- `VsphereEndpointTests.ChangeNetwork_WithNoNetworkNamed_Is500`. `Vsphere/ChangeNetwork` has no
  blank-argument guard, so a request naming no network puts a null into `Dictionary.ContainsKey` and the
  `ArgumentNullException` reaches the middleware unmapped. It is reachable by any caller who may change a
  network at all, and it logs as an unhandled exception. `Proxmox/ChangeNetwork` answers the same request
  400 "An adapter and target network are required"; the `<remarks>` says that is the fix, and nothing is
  reconfigured either way.
- `VsphereEndpointTests.ForAProxmoxVm_AVsphereRouteIsNotRefused`. There is no provider guard on the
  vSphere routes, so a Proxmox VM addressed through one is powered on by `IVsphereService` rather than
  refused. The `<remarks>` argues it both ways - vSphere needs no per-VM connection detail out of the
  database, so there is nothing at the edge to dereference and the real service fails on its own
  connection cache instead - and the cost is only the error the caller gets. If the guard is ever added,
  this is the test that will say so.

The usage log adds a cluster of these rather than one or two, which is what a feature that has never
had a test in front of it looks like. Four are worth knowing about before touching it:

- `Report_WithoutCredentials_Is500`. `GetVmUsageReport.Handler` reads the caller's id in its
  constructor, and `ClaimsPrincipalExtensions.GetId` hands `Guid.Parse` a null when neither the `sub` nor
  the `nameidentifier` claim is there - the `catch` around the first parse does not cover the second. On
  an `[AllowAnonymous]` controller that makes the report the one route an anonymous caller cannot use.
- `Download_ReplacesAnythingOutsideAsciiWithAQuestionMark`. The CSV is written with
  `Encoding.ASCII.GetBytes`, so an accent in a Vm or user name is lost - and not hypothetically for the
  timestamps, since .NET separates the time from AM/PM with a narrow no-break space. Every date in every
  file the endpoint has ever produced has a question mark in it.
- `Download_WithANullSessionName_Is500` and `Download_WithANullIpAddress_Is500`. Both columns are
  nullable and both are used without a check - `SessionName.Length` for the filename fallback,
  `IpAddress.Replace` for the column flattening.
- `Download_WhenDisabled_Is406`. `[Produces("text/csv")]` narrows content negotiation to a media type no
  formatter can write, so on a host with logging off the download answers 406 with an empty body where
  the other seven routes answer 404 with a reason.

The rest of that class's characterizations are permission and query behaviour, and are `<remarks>`ed in
place: the class-level `[AllowAnonymous]`, the not-found check preceding the permission check on all five
id routes, an edit authorizing against the View the session is *already* in, a session created with no
View being stored against `Guid.Empty`, and the report matching its window against whole sessions rather
than against when the activity happened.

The hubs add four of their own, and the first is the one to read before changing anything there:

- `ProgressHubTests.Join_ForAVmTheCallerCannotSee_IsNotRefused`. `ProgressHub` requires authentication
  and then checks nothing at all - it takes a string, never looks at it and never asks who the caller is -
  so any authenticated caller can subscribe to the task progress of any Vm in the system by naming its id.
  What that leaks is a task's type, name, state and progress, and so the fact that a Vm exists under that
  id and what is being done to it. The fix is the check every route that names a Vm already makes: load
  the Vm and put it through `IVmService.CanAccessVm`, refusing with a `HubException` as `VmHub.JoinUser`
  does.
- `VmHubGroupTests.LeaveView_WhenVisibilityNarrowed_LeavesOnlyWhatIsStillVisible`. Leaving is computed
  from the caller's *current* visibility rather than from what the connection joined, so a caller who
  loses a team keeps receiving that team's traffic until the connection drops.
- `VmHubGroupTests.JoinVm_ForATeamMember_AlsoSubscribesTeamsTheVmIsNotOn`. For a caller who is not a view
  admin, `JoinVm` unions in every team they can see in the view rather than only the teams the Vm is on.
  Over-broad rather than a leak - reaching the union at all needs one of the Vm's teams to be visible
  already - and the narrower rule would be `visibility.TeamIds.Intersect(vm.TeamIds)`.
- `VmUsageLoggingServiceTests.Create_TwiceForTheSameVm_LeavesTwoOpenVisitsThatBothGetClosed`. Nothing
  closes an open visit before opening another, and `VmHub.SetActiveVirtualMachine` writes an entry every
  time a console is opened - so a reconnecting client leaves two open rows that a later close stamps with
  the same instant, and the report counts the time twice. Switching between two Vms is the worse half of
  the same bug: a close only ever names the Vm being left, so the first Vm's row stays open forever and
  the report drops it. Both are fixed in the same place. This is the one of the four with a figure a user
  sees attached to it.

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

- **Authorization at the edges of it.** `PlayerService`, `VmService` and `NetworkService` are driven down
  the refusing path as well as the permitting one, and every endpoint class covering an authenticated
  route asserts its 401 and at least one refusal - a 403, or in `BulkPowerOperationEndpointTests` the
  per-VM `"Unauthorized"` a bulk command reports instead. `ProxmoxController` and `VsphereController` now
  each have the full map of which permission every one of their routes asks for, including the ones that
  ask for none beyond team visibility. `VmUsageLoggingSession` has the same map, driven twice - denying
  the pair each route asks for, and denying the opposite pair to show the route still answers. The hubs
  are now in that set too, as far as there is anything to deny: `VmHub.JoinUser` is the only hub method
  that refuses a caller, and its refusals are covered; the rest compute what the caller can see rather
  than deciding yes or no, and `ProgressHub` decides nothing at all - characterized above rather than left
  as a gap.
- **The client's half of the hub contract.** Both hubs are now covered - the group names, the presence
  bookkeeping, the calls into the usage log and the writer behind them, and one round trip over a real
  connection - and all eight controllers have endpoint tests, covering all 82 actions: `VmController`
  (23), `VsphereController` (21), `ProxmoxController` (17), `VmUsageLoggingSessionController` (9,
  including CSV and report generation), `NetworksController` (5), `FileController` (4),
  `HealthCheckController` (2) and `CallbackController` (1). What no test in this repository sees is the
  Angular side: the method names it listens on and the group names it joins are asserted here as the
  strings *the server* uses, and nothing checks that the two agree. Two things on the server side are
  still open as well. `VmHub` is not driven over a live connection, for the routing reason above. And the
  entity-event handlers that broadcast into its groups - `VmUpdatedSignalRHandler` and
  `VmTeamUpdatedSignalRHandler` - have no class of their own: the names they send to are asserted from the
  joining side only, so a handler sending to a team id where a view id was joined would pass everything
  here.
- **The projections.** The AutoMapper profiles run in every endpoint test, but only the projections those
  tests happen to read are asserted.
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
4. ~~Breadth, authorization first, then the endpoint surface by controller.~~ Done. Authorization first:
   `PlayerService`, `VmService` and `NetworkService` each have a class driving their refusing paths. Then
   all eight controllers - `Vm`, `Vsphere`, `Proxmox`, `Networks`, `File`, `Callback`, `HealthCheck` and
   last `VmUsageLoggingSession`, which needed the second `DbContext` migrated and routed per test before
   it could be covered at all.
5. ~~The two SignalR hubs, and with them the usage log's writer.~~ Done. `ProgressHub` as a unit, `VmHub`'s group membership and its presence bookkeeping
   against real PostgreSQL, `VmUsageLoggingService` as the writer `VmHub` drives, and the hubs' edge -
   mapping, authorization and one round trip - over the hosted application. Driving `VmHub` itself over a
   live connection was deliberately left out: it would need `TestDatabaseScope` keyed on the connection id
   rather than on a request header, and the hub's own behaviour is already covered by direct invocation.
6. The entity-event handlers that broadcast into `VmHub`'s groups, which are what is left of the
   application's own surface. The group names are asserted from the joining side already, so this is the
   other end of names the suite knows.
7. Coverage measurement, opt-in and ungated, purely as a map of where the untested risk still is.
