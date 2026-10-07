Player.Vm.Api has an automated test suite in the `src/Player.Vm.Api.Tests` project. This document
details how the suite is built, how to run it, the conventions to follow when adding to it, and -
because the suite is deliberately being grown in stages - what it does not cover yet.

# Testing

The suite contains 1,732 tests. All of them run today; nothing is skipped.

It is built on xUnit v3, NSubstitute and Testcontainers, and needs nothing from the environment except
Docker: no network, no vCenter and no Proxmox cluster. The unit tests need not even that.

Many of the classes are isolated unit tests. They construct the thing under test
directly and substitute its collaborators. `VsphereIsoProviderTests` and `VsphereServiceCommandTests`
are the largest and most important of them: they drive `VsphereService` and its ISO provider through a
substituted `IVimClient`, which is the only seam between those and a live vCenter.

Six of them are the Proxmox driver, and they are substituted a layer lower than that. `ProxmoxService`
builds its own `PveClient` in its constructor out of `IHttpClientFactory.CreateClient("proxmox")`, so
the seam available is the socket rather than an interface, and `Support/FakeProxmoxCluster.cs` is
a cluster that answers requests: the client's route building, its `{"data": ...}` envelope, its typed
model binding and its task waiting all run for real, and a test asserts the request Proxmox would
actually have received. `ProxmoxServiceCommandTests` (power commands, the cluster-wide reads and the NIC
options), `ProxmoxServiceConsoleTests`, `ProxmoxServiceConfigTests`, `ProxmoxServiceIsoMountTests`,
`ProxmoxServiceSnapshotTests` and `ProxmoxServiceGuestAgentTests` divide it by method; the seventh
class, `ProxmoxServiceVmLookupTests`, is database-backed rather than isolated, because
`GetCurrentNodeForVm` and `BulkPowerOperation` are the only two of the interface's twenty-one members
that read one. That every other class passes a **null** `VmContext` is itself the assertion that the
rest do not.

Thirteen classes host the application in process and send real HTTP requests through it. Everything between
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
  here - and since the driver behind that substitute now has seven classes of its own, what this class
  asserts is the request a handler makes of it, not what it does with one.
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
happens in SQL, but they construct the service directly rather than going over HTTP. Eight of the classes
below are built the same way - `VmHubGroupTests`, `VmHubPresenceTests`, `VmUsageLoggingServiceTests`, the
three entity-event handler classes, `CallbackBackgroundServiceTests` and `ProxmoxServiceVmLookupTests` - as
are the four poller classes and `PollLoopSmokeTests`, which makes fifteen in the suite that need a
database without needing a host, against twelve that need a host and twenty-five that need neither.
The pollers need one because writing `HasPendingTasks` and `PowerState` is most of what they do, and a
pass writes through a context of its own: a value read any other way could be one the pass never saved.

Four classes cover the two SignalR hubs, which carry everything the application pushes rather than
answers: console progress while a VM boots, and who else is looking at a machine. A hub is not reachable
the way a controller is - there is no route to assert and no status code to read - so what is under test
is the **group name**, since a name is the whole of the addressing. A hub that joins the wrong group and
one that joins the right one are indistinguishable from either side until a broadcast arrives, and the
other side of every one of these names is a `Clients.Group(...)` somewhere else in the application: in
the vSphere and Proxmox task pollers for `ProgressHub`, in the entity-event handlers for `VmHub` - which
have three classes of their own, described after these - and in the Angular client's own subscriptions for
both.

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

Four classes cover the background pollers that broadcast into `ProgressHub`'s groups and keep
`Vm.HasPendingTasks` and `Vm.PowerState` in step with the hypervisor. They are the loops behind the
spinner, the progress bar and the power indicator, and until recently none of the four had a test: every
other class in the suite substitutes them away, because a `BackgroundService` whose loop returns nothing
and signals nothing is not assertable from outside.

`Support/PollLoop.cs` is what makes one assertable, and reading it first is worth more than reading
any of the four classes. It is the `IServiceProvider` the loop resolves its per-pass scope from, so a pass
is one `CreateScope` - which makes passes countable, and makes a pass asked for past its allowance
refusable with an exception each service's own `catch` already swallows. That refusal is a barrier: the
extra turn does no work and cannot write to the database the test is about to read. Timing is deliberately
not what advances the loop; the intervals are configured to a minute and the service's own `CheckTasks` or
`CheckState` is nudged instead, so what a pass does is deterministic rather than a race. The exception is
a test whose subject *is* the interval, which configures the arm it expects at 25ms and the other at a
minute - four orders of magnitude, so swapping the two in the service fails by timing out rather than by a
hair. `ProxmoxStateService` is the one poller that cannot be given that margin, because it floors its own
interval at one second: its interval tests are a second against a minute, and the short arm is asserted
with a lower bound on elapsed time rather than only an upper one, so a service that stopped honouring the
floor and slept for nothing would still fail. `Support/PollLoopSmokeTests` tests the harness itself
and nothing about any service.

- `TaskServiceTests` covers vSphere's `TaskService`: the property filter it builds, the notification it
  hands a client, which Vms it flags and clears, the state check a finished power task triggers, what one
  unreachable vCenter or one unreadable task costs the rest of a pass, the readiness stamp, and which of
  the two intervals it picks. The moref lookup is asserted per vCenter rather than per moref, since
  `vm-123` exists on every vCenter of any size.
- `ProxmoxTaskServiceTests` covers `ProxmoxTaskService`, the mirror image, and is where the two pollers'
  shared column is pinned from the other side. Each excludes the other provider's machines, and neither
  exclusion is asserted by anything but its own class - so a green run of one says nothing about the other,
  which is why both classes lead with that test.
- `ProxmoxStateServiceTests` covers `ProxmoxStateService`, which is the other half of the Proxmox pair and
  owns the other column: `ProxmoxTaskService` writes `HasPendingTasks`, this one writes `PowerState` and
  `ProxmoxVmInfo.Node`, and the two disagree about which rows are theirs - `Type == VmType.Proxmox` for the
  task poller against `ProxmoxVmInfo != null` for this one - so a row can be in one poller's set and not
  the other's. Its resources come out of `FakeProxmoxCluster` through the real `ProxmoxService` rather than
  being hand-built, because `IsRunning`, `IsStopped` and `IsPaused` are deserialized from PVE's `status`
  field rather than computed from it, and a resource constructed in a test therefore reports
  `PowerState.Unknown` whatever its `Status` says. `IProxmoxService` itself is substituted on top of that,
  so a pass can be made to meet an unreachable cluster or a vmid the cluster lists twice. The class also
  covers `UpdateVm`, the out-of-band entry point that is not a pass at all: the hub and the command
  handlers hand it a single machine and it is serviced by a `MaxDegreeOfParallelism = -1` `ActionBlock`,
  which creates a scope per item and so is counted by the same barrier.
- `VsphereMachineWatcherTests` and `ConnectionServiceTests` cover how vSphere machine state reaches the
  database. Neither is a poller: `VsphereMachineWatcher` long-polls one vCenter's property-collector change
  feed (`WaitForUpdatesEx`), and `ConnectionService` owns the session, one watcher per enabled host, and the
  single persister that writes what the watchers report. Both run against `FakeChangeFeed`, a substituted
  `IVimClient` that plays a script of update sets and then holds the next call open the way vCenter does,
  so `Idle` - every step taken and a call pending - is the barrier a test waits on rather than a sleep. The
  watcher's subject is the caches: truncated initial pages, pruning only after the last one, a missing
  property keeping its value, a stale moref's `leave` not removing a newer mapping, two live morefs sharing
  a uuid both staying mapped (and, when one leaves or changes uuid, the other taking over the uuid's cached
  state), and the periodic re-snapshot (an empty version on the same collector)
  correcting an update the watcher failed to apply and pruning a machine it no longer includes. A session
  replaced under the long-poll is not reported as a watcher error, and a stop during a stalled setup call
  returns at once. The service's is the lifecycle: one login kept for as long as the session probe says it
  is live, a re-login that retires the old session and swaps in a new session object so the watcher
  rebuilds, a login that finishes after a disconnect being logged out rather than installed, the login
  signal being complete exactly while there is a session, a watcher that died being restarted, a disabled
  or removed host draining its watcher and logging out, a host removed mid-login leaving no Load pending,
  a failed write retried until it succeeds, and a row that already matches the cache written - and
  announced - not at all.
  `VsphereServiceCommandTests` also checks that a command run with the session dropped fails naming the
  host rather than with a `NullReferenceException`.

Three classes cover the entity-event handlers, which are the sending end of those same group names. A
change to a Vm never reaches a client directly: `VmContext` raises an entity event on save, MediatR hands
it to one of five handlers, and the handler works out which groups care and broadcasts to them. So every
group name in the application is computed twice - once when a client joins and once when something changes -
and nothing compares the two.

- `VmSignalRHandlerTests` covers the three handlers behind a Vm's own events: created, updated and deleted.
  All three address the same pair of groups per team, the view and the team, so the shared `GetGroups` gets
  the arithmetic - a view named once for two teams that share it, a team whose view player.api does not
  know, a Vm on no team at all. The rest is what each announcement carries, since the payload is the whole
  message: a create's null property list, an update's camel-cased property names, a delete's bare id. The
  update's names are taken off a real save rather than written by hand, so they are the ones EF's change
  tracker produced. A create sends the row as committed rather than the event's copy, read untracked, since
  another handler of the same event can have written the new row's vSphere state and announced it as an
  update first; a row already deleted falls back to the copy. Last are the two states a Vm arrives in - with its teams loaded, or without them, which
  is what every announcement caused by a poller looks like.
- `VmTeamSignalRHandlerTests` covers the two handlers behind a Vm gaining or losing a team, which say "this
  Vm has appeared" and "this Vm has gone" to clients for whom nothing about the Vm itself changed. Their
  one piece of real logic is a suppression, and it is what the class is mostly about: adding a Vm to a
  second team of a view it is already visible in must not tell that view again, and removing one of two such
  teams must not tell it the Vm is gone.
- `EntityEventBroadcastTests` runs the whole path with nothing stubbed between the save and the broadcast -
  a real `SaveChanges`, the interceptor, real MediatR resolving the handlers the application registered,
  and the five handlers themselves. It is the only thing that says the wiring exists: the handlers are
  found by an assembly scan and every exception one throws is caught and logged, so a handler that stopped
  being registered would leave every other test in the suite green. It is also where the events one save
  really raises are pinned - creating a Vm on a team raises two, deleting one raises a cascade, and adding
  a team raises the join row's event only.

`IViewService` is substituted in all three, as it is everywhere else: which view a team belongs to is a
call to player.api over HTTP, and it is the input every one of these names is computed from.

Four classes cover the things every other class substitutes: the clients that talk out of this process, to
player.api and to the identity provider. They are the only classes in the suite whose seam is the socket
rather than an interface - the shared `StubHttpMessageHandler`'s route rules, with `RefusesUnmatched()`,
described below - so the generated `PlayerApiClient`'s routes and deserialization, IdentityModel's token
request and Polly's retry policy all run for real, and what is asserted is the request that went out and
what was made of the answer.

- `ViewServiceTests` covers `ViewService`, the answer to "what view is this team in?" that every group name
  in the application is computed from. Its subject is as much the cache as the two calls: the application's
  singleton `IMemoryCache`, keyed by bare team and view guids with a fifteen-minute sliding expiration and
  no invalidation anywhere, so every answer is a promise held for fifteen minutes after it was last asked
  for. Which status codes are forgiven is the other half - 404 on a team is caught and answered as "no
  view", and cached like any other answer, while nothing else is forgiven and a 404 on a view's teams is
  not either.
- `AuthenticationServiceTests` covers the password grant this API authenticates with. The one decision the
  service makes is when to ask again, and it compares the lifetime the provider *stated* against
  `TokenRefreshSeconds` rather than counting down - so the answer never changes and a token is either
  renewed on every single call or never renewed at all. Both are driven, since which one a deployment gets
  is a configuration value.
- `AuthenticatingHandlerTests` covers the `DelegatingHandler` that puts the token on every outgoing request
  and is the reason an expired one is invisible everywhere else: a 401 invalidates the token and re-sends,
  up to five times. It is the one class in the suite where Polly's policy is under test rather than
  incidental, and it sets `MaxRetryDelaySeconds` to zero so the six attempts cost no wall clock - which
  makes it a test of how many attempts are made and not of how long they take. `appsettings.json` ships
  120, so the waits in a real deployment are 2, 4, 8, 16 and 32 seconds and a request that ends in a 401
  has held its caller for a minute first; that arithmetic is written down in the class's `<remarks>` and
  asserted nowhere.
- `CallbackBackgroundServiceTests` covers what happens after `CallbacksEndpointTests`' 202: a view created
  from a template gets copies of the parent's maps and usage logging session, and a view deleted takes its
  maps with it and has its sessions closed. It is the only class that drives the real background service,
  which is substituted everywhere else because its `ActionBlock` is built in its constructor and nothing a
  request can await says the work is finished - so this class hands an event to the real queue and then
  waits, bounded, for the effect. It needs a database and a scope factory of its own: the service resolves
  a `VmContext` per event out of `IServiceScopeFactory`, and the factory is hand-written rather than
  substituted so that each scope disposes the context it handed out.

`Support/DatabaseHarnessTests` tests the harness itself. Each of its assertions guards a
property the rest of the suite silently relies on and which would otherwise degrade without failing
anything: that the provider really is Npgsql, that every migration is applied, that snake_case casing
and store-generated UUIDs reached the schema, that foreign keys are enforced, that a test sees only its
own rows, that a request writes to the database of the test that made it, and that the usage log is a
second database of its own rather than the same one with more tables in it.

# Running the tests

```bash
dotnet test src/Player.Vm.Api.Tests                                  # needs Docker
dotnet test src/Player.Vm.Api.Tests -- xUnit.DiagnosticMessages=true # prints the provider banner
dotnet test src/Player.Vm.Api.Tests --filter "FullyQualifiedName~.VsphereServiceCommandTests"
dotnet test src/Player.Vm.Api.Tests --collect:"XPlat Code Coverage" # cobertura under TestResults/
```

**Docker must be running.** PostgreSQL is the only database these tests use, and there is deliberately
no in-memory or SQLite fallback - a fallback that quietly swaps the provider reports a green run that
never touched what production uses. Without Docker every database test fails, each naming the reason;
the rest still pass, because the containers are started by the first test that asks for a database
rather than at assembly load. With `-- xUnit.DiagnosticMessages=true` the run prints
`[Player.Vm.Api.Tests] database provider: PostgreSQL (postgres:16-alpine, real migrations)`, which is what
CI greps for. Never set `diagnosticMessages` in `xunit.runner.json`: the run then hangs after the last test.

The leading dot in a filter (`~.VmsEndpointTests`) keeps a class name from also matching every class that
ends with it.

A full run takes about thirty seconds, container start and both sets of migrations included. About five
of those are one test: `CallbackBackgroundServiceTests.WhenTheFirstAttemptFails_TheEventIsKeptAndRetried`
waits out the real first retry delay, which is the shortest one the service has.

A plain run collects no coverage. `--collect:"XPlat Code Coverage"` turns the collector on with the
exclusions in `src/Player.Vm.Api.Tests/coverlet.runsettings` (applied to every run through
`RunSettingsFilePath`), and `scripts/coverage.sh` adds the HTML report and the ranking; nothing gates on
the figure - see Coverage below.

# Build settings

The test project follows the Crucible API test standard (`agent-docs/api-testing/` in the workspace; its
`README.md` is the description of the shared parts, and `CONVENTIONS.md` is how tests are written). Three
files in `src/Player.Vm.Api.Tests` are copied from it byte for byte and are not edited here:
`Directory.Build.props` (imports the repository's own, then sets `TreatWarningsAsErrors` and keeps the
NuGet restore warnings as warnings), `xunit.runner.json` and `coverlet.runsettings`. The pinned test
packages (`xunit.v3` 3.2.2, `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 18.8.1,
`coverlet.collector` 10.0.1, `NSubstitute` 6.0.0, `Testcontainers.PostgreSql` 4.15.0,
`Microsoft.AspNetCore.Mvc.Testing` 10.0.1) live in `Directory.Packages.props`. The suite runs in VSTest mode
of `dotnet test`, as every Crucible API does; the standard's README says why.

`TreatWarningsAsErrors` is what enforces the xUnit analyzers that ship with `xunit.v3`. These fail the
build:

- xUnit1013 - A public method on a test class with no `[Fact]`.
- xUnit1026 - A `[Theory]` parameter the test does not use.
- xUnit1051 - An awaited call that does not take the test's cancellation token.
- xUnit2000 - `Assert.Equal` with the expected value passed second.
- xUnit2012 - `Assert.True` over a collection lookup, where `Assert.Contains` says what failed.
- xUnit2029 - `Assert.Empty` over a filtered collection, where `Assert.DoesNotContain` says what was
  found.
- xUnit2031 - a `Where` before `Assert.Single`, where the `Assert.Single(collection, predicate)` overload
  says which element was expected.

The last two are syntactic rather than semantic, which is worth knowing before rearranging an assertion:
`Assert.Empty(log.At(LogLevel.Error))` builds only because the `Where` is inside `RecordingLogger.At`, so
inlining that helper breaks the build and the fix is `Assert.DoesNotContain` rather than un-hiding the
filter.

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
`System.Security.Cryptography.Xml` through the health-check UI packages. Check where it stands when
bumping a dependency:

```bash
dotnet list package --vulnerable --include-transitive
```

Package versions live in `Directory.Packages.props`, and `CentralPackageVersionOverrideEnabled` is false
so that a project cannot pin itself to a different version of a shared package.
`Microsoft.AspNetCore.Mvc.Testing` and `Microsoft.AspNetCore.SignalR.Client` (an app extra, for
`HubConnectionTests`) track the ASP.NET Core version rather than the test tools around them.

# How the harness works

## Layout

The test project mirrors `Player.Vm.Api`: a test of `Features/Vms/VmController` lives in
`Features/Vms/`, of `Domain/Proxmox/Services/ProxmoxService` in `Domain/Proxmox/Services/`, of
`Infrastructure/HttpHandlers/AuthenticatingHandler` in `Infrastructure/HttpHandlers/`, each in the
matching `Player.Vm.Api.Tests.<folder>` namespace. (Inside those namespaces `Domain.` and `Features.`
resolve to the test project's folders, so references to production types are written in full,
`Player.Vm.Api.Domain.Models.Vm`, or through an alias such as `VmEntity`.) `Support/` holds the harness:

- `Support/Shared/` - the standard's shared files, byte-identical in every Crucible API and never edited
  here: `PostgresTestDatabase`, `ITestDatabase`, `TestDatabaseScope`, `DatabaseTestBase<TContext>`,
  `ApiTestBase<TContext>`, `TestAuthHandler`, `HubRecorder`, `HubHarness`, `StubHttpMessageHandler`,
  `RecordingLogger`, `Waits` (namespace `Crucible.Api.Testing`). `agent-docs/api-testing/sync.sh --check`
  reports any difference.
- `Support/` - the files built from the standard's templates: `DatabaseFixture`, `VmContextFactory`,
  `VmAppFactory`, `TestConfiguration`, `DatabaseTestBase`, `ApiTestBase`, `TestActor`, `TestData`,
  `ClaimsPrincipalBuilder`, `AuthorizationHarness`, `TestMapper`, and the harness self-tests
  `DatabaseHarnessTests`, `HttpHarnessTests` and `TestActorTests`.
- `Support/` also holds vm.api's own extras, each named for what it fakes: `FakeProxmoxCluster` (a PVE
  cluster at the socket), `FakeChangeFeed` (vCenter's property collector), `PollLoop` (a counted turn of a
  polling `BackgroundService`, with `PollLoopSmokeTests`), `PlayerDirectory`, `PlayerHarness` and
  `PlayerServiceSpy` (player.api's side of the seam, below), `RecordingHubHarness` and `HubContextHarness`.

## The database

`Support/DatabaseFixture.cs` owns the databases for the whole run, as an assembly fixture declared in
`AssemblyFixtures.cs`. It is a thin wrapper over two shared `PostgresTestDatabase` instances, because
production has two databases: `VmContext` reads `ConnectionStrings:PostgreSQL`, and `VmLoggingContext`
reads `VmUsageLogging:PostgreSql`, a separate connection string with its own migration history and, as
often as not, a separate server. Each instance starts its own `postgres:16-alpine` container on the first
test that asks for a database, migrates a template (`vm_template`, `vm_logging_template`) with the real
migrations (both live in the API assembly, so no migrations assembly is named), and hands each test a
clone made with `CREATE DATABASE ... TEMPLATE`, a file-level copy that costs milliseconds. A session is
the pair: `ITestDatabaseSession<VmContext>` for the shared bases, and also
`ITestDatabaseSession<VmLoggingContext>`, which is how a request resolves the usage log and how
`NewLoggingContext()` reaches it. `DatabaseHarnessTests` asserts the separation from both ends.

The shared `PostgresTestDatabase` clears only the template's connection pool after migrating
(`CREATE DATABASE ... TEMPLATE` fails while any session is connected to the template, and a disposed
`NpgsqlConnection` returns to the pool rather than closing), and drops each test's database
`WITH (FORCE)` afterwards. Isolation is a database per test rather than a rolled-back transaction on
purpose: the `EntityEventInterceptor` publishes entity events on `TransactionCommitted` and discards them
on rollback, so a transaction per test would silently stop every entity event.

Tests reach this through `Support/DatabaseTestBase.cs`: `Db` (a context over the test's own database),
`NewContext()` for re-reading through a cold change tracker, `Seed(...)`, `Mediator` (the substituted
mediator the test's own contexts publish entity events to), `Ct`, `WaitUntil`, and `NewLoggingContext()`
for the usage log, deliberately not a `Db` of its own because almost no test needs it.

## The host

`Support/VmAppFactory.cs` is a `WebApplicationFactory<Startup>` in the standard's **per-class** variant:
one host per test class, through `IClassFixture<VmAppFactory>`, because the tests arrange and assert on
the NSubstitute doubles the host resolves. Its doc comment is the authoritative list of what is real; the
shape of it is:

- **Substituted**: `IVsphereService` and `IProxmoxService` (the hypervisors), `IPlayerApiClient` (the
  generated player.api client, answered by `Factory.Player`; see Callers and permissions), `IViewService`
  (`ViewService`, player.api by its admin client: its team-to-View lookup is answered by `Factory.Player`,
  the entity-event handlers' View lookups are arranged per test), `ITaskService` / `IProxmoxTaskService` (the
  pollers the `CheckTasks` pipeline behaviors poke after a power command), `ICallbackBackgroundService`
  (the webhook send queue) and `IIsoProvider` (ISO storage on a hypervisor).
- **Replaced**: token validation (the shared `TestAuthHandler`), `IHttpClientFactory` (the shared
  `StubHttpClientFactory` over `Factory.OutboundHttp`), and the `VmContext` and `VmLoggingContext`
  registrations (below).
- **Removed**: every `IHostedService`. The background pollers would otherwise start dialing a vCenter
  that is not there, on their own schedule, in the middle of unrelated tests.
- **Real**: everything else, including `PlayerService` (every authorization and visibility decision),
  `VmService.CanAccessVm`, the handlers' own permission gates and the SignalR hub contexts
  (`HubConnectionTests` connects real clients to them).

A substitute is safe in a per-class host because one class's tests run one after another, so no other
class's request reaches it. Its state does persist across those tests, so `ApiTestBase` resets the
player.api side for every test (`Factory.ResetPlayer()`: the client and `IViewService` substitutes cleared,
the directory emptied, the host's `IMemoryCache` cleared) and each endpoint class clears the rest of what it
arranges in `InitializeAsync`: a `Received()` assertion in a class that did not clear can be satisfied by an
earlier test's request. A run-wide host would need recorders instead, which is the standard's default and
the reason it is not used here.

Two of the substitutes are worth knowing the reason for, because neither is about avoiding the network:

- `ICallbackBackgroundService` is substituted because the real one is a `BackgroundService` that builds
  its `ActionBlock` in its **constructor**. Removing the `IHostedService` registrations does not stop it:
  handing it an event still starts processing, on a thread pool thread, outside any request - so it
  resolves the host's own `VmContext` and races the test asserting on the row it just wrote.
  `CallbackBackgroundServiceTests` covers the real one, outside the host, with a scope factory of its own.
- `IIsoProvider` replaces only the storage. `IsoService` itself is left real, because the scope
  resolution, the permission gates, the filename sanitizing and the cross-provider merge are the parts
  worth running. The substitute starts *disabled*, which is what an install with no ISO storage
  configured looks like; `EnableIsoProvider()` turns it into a plausible hypervisor.

The host runs in the `Production` environment, so `ExceptionMiddleware` answers a 500 as deployed: the
exception message in `detail`, the title "A server error occurred.". (It writes the `ProblemDetails` as
`application/json`, so the shared `AssertProblem` is not used here; tests read `title` and `detail`
themselves.) The configuration it layers over `appsettings.json` is `Support/TestConfiguration.cs`, with a
comment per key: the health-check UI off, `IsoUpload:MaxFileSize` small enough that a test can exceed it
(it is at once the ceiling `UploadIso` enforces and the multipart body limit `Startup` hands the form
reader), and `Proxmox:Host` set to `pve.test`, the provider instance id every Proxmox `ViewNetwork` row is
keyed on. What varies per host is set by the factory itself through `UseSetting`: the databases,
`VmUsageLogging:Enabled` and `Authorization:AuthorizationScope`.

One subclass exists for a setting the application reads at **startup**, which no test can flip:
`VmUsageLoggingEnabledFactory` runs with `VmUsageLogging:Enabled` true. `Startup.ConfigureServices` reads it
once to choose which `IVmUsageLoggingService` to register, and `VmUsageLoggingSessionController` captures
`IOptionsMonitor.CurrentValue` in its constructor, so rewriting configuration mid-run would prove nothing
about a deployment. The default host keeps what `appsettings.json` ships, so every other class tests the
shipped configuration.

The privileged caller of `CallbackController` (player.api's webhook) needs no host of its own:
`CallbacksEndpointTests` sends its requests as an actor whose client carries the shared `X-Test-Scope`
header with the ordinary scopes plus `Authorization:PrivilegedScope`, and asserts the 403 for `Root`, whose
token carries the ordinary scopes only.

## How a request finds its database

`AddEventPublishingDbContextFactory` and `AddDbContextPool` bake one connection string into pooled
options when the container is built, so the application's own registrations cannot choose a database per
request. The factory replaces both with a scoped registration that resolves the test's session through
the shared `TestDatabaseScope.Resolve`, keyed on the `X-Test-Session` header every client from
`ApiTestBase` carries; the request scope is the context's `ServiceProvider`, as in production, so entity
events reach the real handlers. A header rather than an `AsyncLocal`: a lookup that misses fails loudly
and names the request it could not route.

Both registrations are the shared two-argument `TestDatabaseScope.ReplaceRegistration`, given
`() => _started ? null : _hostSession`. `Program.Main` matches neither convention `HostFactoryResolver` looks
for, so `WebApplicationFactory` invokes `Main` on a background thread, and `Main` calls `InitializeDatabase`,
which resolves both contexts with no request in flight and migrates them. So the host gets a pair of
throwaway databases cloned from the templates (the migration is then a no-op), and a resolution outside a
request reaches them until `CreateHost` returns; after that it throws. `HttpHarnessTests` asserts that a
request never lands there, that a request with no session header is refused, and that a context resolved
outside a request after startup throws.

The limit of that mechanism is the hubs. A hub invocation is not an HTTP request, so a hub method
resolving a `VmContext` gets the host's own throwaway database rather than the calling test's. That is
why `VmHub` is driven by direct invocation over the test's own context and only `ProgressHub`, which
touches no database, is driven over a live connection in `HubConnectionTests`.

## Callers and permissions

vm.api keeps no users or permissions of its own: `PlayerService` turns what player.api answers into every
authorization and visibility decision. The seam is therefore the generated `IPlayerApiClient`, the one
thing that leaves the process (`CONVENTIONS.md` section 1, the sibling-API seam), and `PlayerService` is real
in every test, hosted or not.

`Support/PlayerDirectory.cs` is player.api's side: which View each team is in, and per user the system
permissions (`GetMyPermissionsAsync`) and the team permission claims (`GetMyTeamPermissionsAsync`) player.api
would return, plus the teams and Views by id and `IViewService.GetViewIdForTeam`. It answers as player.api
does where vm.api's decisions depend on it: `GetUserViewTeamsAsync` is the user's primary visibility context
(no primary team is no team; `ViewView` or `ManageView` on the primary team is every team of the View;
`ViewTeam` or `ManageTeam` is the primary team and the teams scoped from it; otherwise the primary team
alone); the privileged `GetViewTeamsAsync` answers 403 unless the caller holds `ViewViews` or `ViewView` on
the View, and `GetViewsAsync` unless it holds `ViewViews`; a scoped claim carries the effective permissions of
the membership that grants it. The `GetMy*` routes answer for the user of the request in flight
(its `sub` claim), and a request with no credentials is refused with a 401, as player.api refuses the empty
`Authorization` header vm.api forwards for it. A View nobody added answers 404 on its routes, so a test's
Views have to exist in the directory, which the arrangement helpers see to.

An actor (`Support/TestActor.cs`) is the identity a request carries (the `sub` and `name` claims the shared
`TestAuthHandler` mints) and the player.api data its builder steps write under its id:

- `Holding(AppSystemPermission...)` is a system permission.
- `Holding(AppTeamPermission, teamId)` is a claim on the team carrying it, the actor a member; a team not
  yet in a View gets one of its own.
- `Holding(AppViewPermission, viewId)` is the permission on the claim of one of the actor's own teams in the
  View (its primary team there, else a team it is a member of, else a team of its own), which is where
  player.api reports a View permission.
- `InView(teamId, viewId)` places a team; the actor's first membership in a View is its primary team there,
  as player.api assigns it, unless `PrimaryOn(teamId)` names another. The primary team is what visibility
  comes from (`ViewView` or `ManageView` on it sees every team of the View, `ViewTeam` or
  `ManageTeam` the teams scoped onto it with `SeesScoped`); `MemberOf(teamId)` is a membership with nothing
  on it.
- `Factory.Player.Sees(userId, viewId, primary, canViewAllTeams, teams)` writes the data a given visibility
  comes from, for the tests that are about what a caller sees rather than what it holds.

`Root` (`ApiTestBase`) is a fresh actor every test, holding every system permission and nothing else. A gate
that asks only for a View or team permission (the vSphere and Proxmox snapshot and guest-file routes, the
bulk revert, the ISO View upload and listing) needs Root on that View too, which those classes arrange
(`Actor().WithId(Root.Id)...` adds to Root). The vSphere and Proxmox classes also take the two network system
permissions from Root, so `NetworkService` takes the branch a caller without view-network access takes,
which is what their network tests are about. `Client()` carries no identity, for the 401 cases.

**The cache.** `PlayerService.GetSystemPermissionsAsync` keeps a user's system permissions in `IMemoryCache`
for a minute, keyed on the user id (`PlayerService.cs:515-521`), and the per-class host's cache is one
singleton for every test of the class. Both answers apply: `Root` is a fresh id per test, and
`Factory.ResetPlayer()` clears the cache before every test, so even a user id reused across tests reads its
own test's permissions. `HttpHarnessTests.A_user_id_reused_across_tests_sees_this_tests_system_permissions_first`
and `_second` pin it: one fixed id, `ViewVms` in one test and `ControlVms` in the other, each asked twice.
Within a test an actor's answers are read once and kept, so a test never changes an actor's system
permissions after its first request.

Each gate is tested from both sides with an actor: allowed for a caller holding exactly the permission it
asks for on the resource's own team or View
(`Get_is_allowed_for_a_caller_holding_exactly_ViewTeamVms_on_the_Vms_team`), and refused for one holding that
permission on another team or View, or its neighbour
(`Delete_is_forbidden_for_a_caller_holding_ManageTeam_only_on_another_team_and_leaves_the_row`). The route
classes also keep a wrong-question denial per gate family: `AllBut(permission)` in the vSphere and Proxmox
classes is a caller holding everything Root does but the asked permission, and `Deny(system, view)` in the
usage-log class takes the asked system permission from Root.

Service tests that construct a service directly use `Support/PlayerHarness.cs`: the real `PlayerService` over
a substituted client and `IViewService` answered by a directory of its own, for one caller
(`_player.Grant(b => b.Holding(...))`, `_player.Sees(...)`), with a new `PlayerService` each time
`_player.Service` is read so that nothing is cached from before the arrangement. Where a test's subject is
how a service asks rather than what it is told (one batched question rather than one per team, no elevated
check for the caller's own Vm), `Support/PlayerServiceSpy.cs` wraps the real service and records each call;
every answer is still the real one. `PlayerServiceAuthorizationTests` covers `PlayerService` itself against
the client, and `TestActorTests` pins what the real `PlayerService` derives from each builder step (the
step's permission passes and its near misses do not) and the directory's fidelity to player.api: the primary
team, the View teams, the gated listings and what a scoped claim carries.

## The fakes that are not in the standard

- The out-of-process clients (the generated `PlayerApiClient`, IdentityModel's token request, `PveClient`
  through `Support/FakeProxmoxCluster.cs`) run over the shared `StubHttpMessageHandler`, one per test,
  arranged with its route rules (`Answers`, `AnswersOnce`, `AnswersJson`, `Throws`) and
  `RefusesUnmatched()`: a request nothing arranged **throws**, because a 404 would be swallowed by the error
  handling several of these tests are about. It replaced vm.api's own `RoutedHttpHandler`.
- The hub tests use the shared `HubHarness` (`JoinedGroups`, `LeftGroups`, a connection id of the test's
  choosing). `Support/RecordingHubHarness.cs` remains for `VmHubPresenceTests` alone: it keeps one ordered
  log of every send with the list of groups it was addressed to, because the presence tests assert the
  first and last `ActiveVirtualMachine` across groups and whether one send named several groups, which the
  shared per-group proxies and `Sent<T>` do not express.
- `Support/HubContextHarness.cs` records what something outside a hub (an entity-event handler, a task
  poller) sends through an `IHubContext<THub>`. It remains beside the shared `HubRecorder`, which has its
  `Recipients` and `FailsFor`, because these tests also read what the recorder does not keep: one ordered log
  of every send with its group and arguments (`Sends`, asserted empty for "nothing was sent at all"), every
  send of one method across groups (`Of`, counted for "announced twice"), and `Clients.All` among the
  recipients. A `HubRecorder` reader for the ordered log would let it go. The host itself keeps the real
  hub contexts.
- `Support/TestMapper.cs` is the application's real AutoMapper configuration, built through DI as
  `Startup` builds it (`ConsoleUrlResolver` needs `ConsoleUrlOptions`, registered with recognizable urls).

## The harness self-tests

`Support/DatabaseHarnessTests`, `HttpHarnessTests` and `TestActorTests` check what the rest of the suite
assumes: two tests inserting the same network id into `view_networks`' unique
`(view_id, provider_type, provider_instance_id, network_id)` index do not see each other (and a second
insert in one test is refused, which proves the index), explicit ids survive, saving publishes entity
events, the real migration history and snake_case naming are in force, the usage log is a second migrated
database, foreign keys are enforced, a request with no identity is a 401 and a caller with no permissions
a 403, a request reaches its test's database and never the host's, and each actor shape answers as it
promises. When the harness breaks, these fail instead of a hundred unrelated tests.

# Adding a test

The rules are the standard's `CONVENTIONS.md` (`agent-docs/api-testing/` in the workspace), which the
reviewer judges against; what follows is how they land in this repository.

1. Put the file where the code under test lives, mirroring `Player.Vm.Api` (see Layout above), in the
   matching `Player.Vm.Api.Tests.<folder>` namespace. The harness and its self-tests live in `Support/`.
2. Prefer a unit test with substituted collaborators for anything below the HTTP layer. Derive from
   `DatabaseTestBase` when the assertion is about what was stored, and from `ApiTestBase` (plus
   `IClassFixture<VmAppFactory>`, or one of its subclasses) when it is a contract that has to survive the
   handler, the serializer and the wire - a status code, a response body shape, an authorization outcome:
   `public class XTests(DatabaseFixture fixture, VmAppFactory factory) : ApiTestBase(fixture, factory), IClassFixture<VmAppFactory>`.
   Both cost a database; a unit test does not. A denied test's caller is a near miss seeded with
   `Actor().Holding(...)`, never a caller holding nothing, and its name says what the caller holds
   (`DeleteMap_is_forbidden_for_a_caller_holding_only_ViewTeamMaps_and_leaves_the_row`). For a hub method,
   attach the shared `HubHarness` and invoke it directly: the group names are what a hub test is about,
   and only the hub's edge - its mapping, its authorization, a round trip - is worth a live connection.
   For an entity-event handler, construct it over `Db` with a `HubContextHarness` and call `Handle` with
   the notification a real save published - `DatabaseTestBase.Mediator` has recorded them - rather than
   with one written by hand. For anything whose subject is a call *out* of this process, put
   a `StubHttpMessageHandler` of the test's own (with `RefusesUnmatched()`) under the real client instead of
   substituting the client: what is worth asserting
   there is the request that went out.
3. Name the method as a sentence. The failure summary is all a reader of CI output gets.
4. Pass a cancellation token to anything awaited, including inside private helpers - `Ct` on the two
   base classes, `TestContext.Current.CancellationToken` elsewhere. xUnit1051 only sees test methods,
   but a helper that hangs hangs the run just the same. A token is not enough where the thing awaited is
   a `TaskCompletionSource` the test itself completes - nothing will cancel it - so bound those with
   `WaitAsync(TimeSpan, Ct)`, as `HubConnectionTests.Arrives` does. Where the effect being waited for is a
   row rather than a message, poll for it with a deadline and `Assert.Fail` past it, as
   `CallbackBackgroundServiceTests.Eventually` does: work handed to a queue has no completion to await, and
   a missing effect has to fail rather than hang.
5. Scope anything from `NewContext()` with `await using`. One PostgreSQL server serves the whole run,
   and an undisposed context keeps its pooled connection checked out until the process exits.
6. Do not add Arrange, Act and Assert comments. A blank line already shows the shape of a test.
   Comments should explain what a test pins and why it matters, not what the next line does.
7. For an authorization test of a service, construct the service with `PlayerHarness.Service` (the real
   `PlayerService`) and `TestMapper.Mapper`, and arrange the caller as player.api data with
   `_player.Grant(...)`. Never substitute `IPlayerService`: it is the code that decides.
8. Break the production code and check the test fails, then restore it (copy the file aside first, or
   `git checkout -- <file>` only if it was unmodified). For an authorization gate this is the standard's
   broken-gate check (`CONVENTIONS.md` section 5): break the gate at that endpoint's call site so it always
   allows, every copy where the path checks it more than once. A test that
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
   eight, the four hub classes together with `VmUsageLoggingServiceTests` in eighteen, and the three
   entity-event handler classes in fourteen:
   disabling all four checks in Proxmox's `BaseHandler.GetVm` at once, for instance, should fail exactly
   39 cases across exactly five tests, and any other total means a mutation landed somewhere it was not
   aimed. Predict the count and the names before running, and treat a surplus as a finding: dropping
   vSphere's `PowerOnVm` call reddens one row of the power theory *and* the Proxmox-VM characterization
   test, because that test drives the same route. Mutating the connection id that `ProgressHub.Join` adds
   reddens `HubConnectionTests`' live-connection tests as well as the unit ones it was aimed at, for the
   same kind of reason: the real client never joins the group at all. Dropping the
   `teamId != notification.Entity.TeamId` guard in `VmTeamUpdatedSignalRHandler` reddens
   `EntityEventBroadcastTests.DeletingAVm_AnnouncesTheDeleteForTheVmAndForEachTeamItWasOn` as well as the
   create tests it was aimed at, and explaining that established something about EF worth knowing: a
   cascade delete leaves the deleted join row in the parent's collection at the moment the events are
   published, so on a delete that guard is doing the same work it does on a create. A surplus that turns out
   to be explainable is still worth the minute it takes to explain, because the other reading of it is a
   mutation that landed in two places.

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

   Mutation testing answers "does anything assert this", which means it can only be asked about a line
   something already runs. `scripts/coverage.sh` answers the other one - what nothing runs at all - and is
   worth running after finishing a class, because the line it names as unexecuted is usually a branch the
   arrangement never produced rather than one anybody decided to leave out. It found four of those, all
   since covered; they are listed under Coverage below, along with what each turned out to be.

A defect found while writing a test is characterized by an ordinary passing test of the current
behaviour, and described in the app's defect document, `agent-docs/api-test-bugs/vm.api.md` in the
workspace (outside this repository): the test, the production file and line, what the caller sees, the
proposed fix and the test to flip once it lands. The test itself carries at most a one-line `<summary>`
stating the current behaviour, and no defect note in any comment; `agent-docs/api-testing/verify.sh`
checks both. Behaviour that may be intended goes under the document's "Needs a decision".

Not every oddity is a defect. `VsphereServiceCommandTests` pins a contract that reads as sloppy error
handling and is not: the VM UI lets a user multi-select machines and hit power on, so one machine that is
already on - or one whose host is unreachable - must not surface as an error for the whole selection,
which is why the service reports outcomes as opaque strings and swallows some faults on purpose. Those
comments carry intent through the next refactor and stay.

Two more were found by mutation rather than by reading, and are the reason the convention in "Adding a
test" is worth the round trip: removing `_runningTasks.Clear()` and removing `_tasksPending = false`, both
of them a single line at the top of a pass, each reddened *nothing* in a class that already had 37 cases.
Neither is a defect - both lines are correct - but nothing asserted what they were for, and what they are
for is large. Without the first, every task the process ever saw is rebroadcast to its group on every pass
forever, a progress bar frozen at whatever percentage the task was on when it finished. Without the second,
the first task anybody starts pins every vCenter on the fast interval for the life of the process.
`ATaskThatHasLeftTheRecentList_IsNotBroadcastAgainByTheNextPass` and
`OnceNothingIsRunning_TheNextPassComesOnTheSlowIntervalAgain` are what came of it. Both needed a two-pass
arrangement, which no other test in the class had; cross-pass state was the shape of the gap, not those two
lines.

Two conventions are not yet uniform across the suite and are follow-ups rather than rules: test names
mix the sentence-with-underscores form the newer tests use with older PascalCase names, and several
classes build their entities inline rather than through `Support/TestData.cs`.

# Continuous integration

`.github/workflows/build-and-test.yml` is the standard's workflow, copied by `agent-docs/api-testing/sync.sh`
with this repository's test project filled in and not edited here (it replaced the earlier `test.yml`). It
runs on pull requests and on pushes to `main`: restore, build the solution (which also compiles
`Player.Vm.Api.Client` under `TreatWarningsAsErrors`), and
`dotnet test src/Player.Vm.Api.Tests -- xUnit.DiagnosticMessages=true`, then confirms the log carries
`[Player.Vm.Api.Tests] database provider: PostgreSQL`, so a harness change cannot silently stop exercising
the production database. The SDK comes from `global.json`. There is no database service block:
`ubuntu-latest` runners ship Docker, and Testcontainers starts and disposes the containers itself. The TRX
of the run is uploaded as `test-results` even when the job fails. Coverage is not collected there.

`coverage.yml` is the other workflow that runs the suite, and it is `workflow_dispatch` only: nothing it
produces is visible to any other run, and it cannot fail a pull request. The next section is what it is for.

# Coverage

`scripts/coverage.sh` runs the suite with coverage and prints the one thing it exists to say: the
classes with the most lines that nothing executed, most first. It writes an HTML report to
`coverage/report/index.html` alongside it. `.github/workflows/coverage.yml` is the same script on a
runner, started by hand from the Actions tab, and it publishes the table to the run's summary page
and the report as an artifact.

```
scripts/coverage.sh                              the whole suite
scripts/coverage.sh --filter VmSignalRHandler    anything further goes to `dotnet test`
TOP=50 scripts/coverage.sh                       a longer ranking; the default is 25
```

A filtered run maps only what those tests reach, so everything else in it reads as 0%. That is the
useful form when the question is "what does this class of mine not touch" and a misleading one for
anything else; `coverage/` is rebuilt from scratch each time, so the last run is the only one there.

None of it is part of a normal run or of the build that gates a pull request. `coverlet.collector` is
a VSTest data collector, so it is inert until a run asks for XPlat Code Coverage, and only the script
and that workflow ask: `dotnet test` neither instruments nor slows down, and `build-and-test.yml` never
collects a figure or sees one. The single extra tool, ReportGenerator, is pinned as a local tool in
`.config/dotnet-tools.json` and restored by the script. `jq` formats the ranking, and the script says
so and carries on without it if it is not installed.

## Why there is no threshold

Deliberately, and it is the part of this most worth keeping. A percentage attached to a merge button
changes what people write, because the cheapest way to move it is a test that drags an untested file
through without asserting anything about it - which is the exact opposite of convention 8 above,
where a test earns its place by being watched to fail, and every class here has been through that.
A coverage gate would reward the one kind of test that never has to be.

The second reason is that the figure is not a fact about the code on its own. Coverage says what
nothing *executed*. Mutation testing says what nothing *asserts*, and the two find different things:
`Startup` reports 94.3% covered because every endpoint test hosts the application, and almost nothing
in the suite asserts anything about it at all. Read a coverage number as a lower bound on the untested
surface, never as an upper bound on the tested one.

So: a map for deciding what to test next, and nothing else. It has no vote on whether a change merges.

## What is measured

`src/Player.Vm.Api.Tests/coverlet.runsettings` (the standard's shared file) holds the settings, and they
matter to any figure quoted from it. The test assembly and any `*.Migrations.PostgreSQL` assembly are
excluded by name; EF's migrations under `Data/Migrations` are excluded by file, because the
harness migrates the template database once per run and would report them as well covered while
proving nothing; auto-implemented property accessors are skipped, so that a number does not move
whenever a DTO grows a field; generated and `[Obsolete]` members are out; and so is the test assembly,
whose own coverage would be near total by construction.

One surprise in the output is not a mistake: the `Crucible.Common.EntityEvents` types are listed
because that package compiles into this assembly rather than shipping as one. `EntityEventInterceptor`
at 66.4% is code this repository's tests can and do exercise - `EntityEventBroadcastTests` is what
reaches it - not a dependency's internals leaking into the report.

## The shape of it

As of the run after roadmap item 12: **72.5% of lines** (6,436 of 8,872 coverable), 69.0% of
branches, 80.0% of methods, across 166 classes. That single number is close to meaningless on its
own, because of where the untested lines are:

```
  Features                        96.8%       104 untested of 3,236
  Domain.Proxmox                  90.2%        95 of 973
  Domain.Models                   86.5%        12 of 89
  Domain.Services                 84.8%       120 of 789
  Infrastructure                  75.4%       135 of 548
  Crucible.Common (in-assembly)   68.1%        79 of 248
  Domain.Vsphere                  29.2%     1,856 of 2,622
```

1,951 of the 2,436 untested lines - 80% of them - are still the two hypervisor drivers, but that gap is
no longer one thing, it is no longer the whole of either namespace, and it is now almost entirely one of
the two. `Domain.Proxmox` went from 9.0% to 90.2% without a cluster, because the Proxmox driver's seam is
a substituted `HttpClient` rather than an interface - `ProxmoxService` alone went from 3.7% to **99.4%**
(508 of 511 lines), and both of its background services are at 100%. `Domain.Vsphere` went
from 15.6% to 29.2% for a different reason: not the driver, which can still only be reached through
`IVimClient`, but the two pollers above it, which needed no vCenter at all and are now at 100%. What is
left in each namespace is the client and the connection cache, plus the ISO upload path on the Proxmox
side. The application's own request-handling surface, the `Features`
tree, is at 96.8%, and `Domain.Services` - which is where the four out-of-process clients live - went from
48.3% to 83.9% when they were covered. Whatever this suite is short of, it is not breadth over the code
that answers a request, and it is no longer breadth over the code that calls out of the process either.

That is also why the script ranks by *count* rather than by percentage. The question a reader has is
"how much untested code is in here", and the two orderings disagree: by percentage the dead eighteen-line
`Player.Vm.Api.Hubs.VmHub` sorts above `VsphereService`, and by count it is 1,169 lines behind it.

## What the first run found

Most of what the ranking surfaced in the covered part of the tree turned out to be already written
down. `VmController.Get`'s own `if (vm == null) return NotFound(vm)` is unexecuted, and
`VmsEndpointTests.Get_ForAnUnknownVm_Is404WithAProblemDetailsBody` already says that branch is
unreachable because the service throws first; the `throw new InvalidOperationException()` in `Create`
and `Update` is unexecuted, and `[ApiController]` answering 400 before the action runs is already
characterized. `Player.Vm.Api.Hubs.VmHub` at 0% is the dead second copy of the hub that
`HubConnectionTests` asserts is not mapped. A 0% class is not automatically a gap, and coverage
agreeing with a `<remarks>` written from reading the code is worth something on its own.

Four things were genuinely new, and they are all narrower than a class. All four are covered now - this
is the record of what a measurement found that reading the code had not:

- **`vms/actions/power-off` had no test.** It was the only one of the five bulk routes without one -
  power-on, shutdown, reboot and revert all had theirs - and the action body is five lines that set
  `PowerOperation.PowerOff` and send the command. The three hard-power routes are now one theory,
  `BulkPowerOperationEndpointTests.EachHardPowerRoute_SendsItsOwnOperation`, so the operation each route
  sends is asserted per route rather than for the one that happened to have tests.
- **No Proxmox Vm had ever been through the bulk power path.** The `vm.Type == VmType.Proxmox` accept
  arm and the `IProxmoxService.BulkPowerOperation` dispatch after the loop were both unexecuted, so
  everything asserted about bulk power was asserted about vSphere machines only. Two of the per-VM
  outcome strings were unreachable for the same reason - "Unsupported Operation" for a Proxmox revert,
  and for a Vm of neither type - and `"Insufficient Permissions"` was never produced by any bulk test,
  though `"Unauthorized"` was. `BulkPowerOperationEndpointTests` now has a Proxmox region - including a
  mixed batch, which is the case that shows the two dispatches are independent rather than exclusive -
  and a permissions region for the three refusals.
- **`VmHub.JoinUser`'s team-scoped active-Vm branch.** For a caller who could not view all teams, the arm
  that reports the active Vm when it is on the team being joined never ran; the view-admin arm beside
  it did. `VmHubGroupTests.JoinUser_ForATeamMember_ReportsAnActiveVmOnThatTeam` is that arm, and asserts
  the view service is not consulted at all - resolving views is the admin arm's job, and a member
  reaching it would be one player.api call per subscribed user.
- **`VmTeamDeletedSignalRHandler`'s suppression loop never kept looking.** Every delete test that
  reached the loop matched on the first team it examined, so the path where another team of the Vm
  resolves to a *different* view - or to none - and the loop carries on rather than suppressing the view
  send was unexecuted. The create handler had exactly that test, in
  `VmTeamSignalRHandlerTests.Created_WhenAnotherTeamOfTheVmIsInAnotherView_StillTellsTheView`; the
  delete handler did not. An asymmetry between two test classes with nothing to see for it in the
  production code, and not something the fourteen mutation runs could have shown: a mutation of that
  guard reddens the tests that do reach it, which is exactly what hides the path that none of them takes.
  `Deleted_WhenNoOtherTeamOfTheVmIsInTheSameView_StillTellsTheView` is the missing one, as a theory over
  both absences - the other team in another view, and in none.

The rest of the ranking was the out-of-process integrations, untested because none of them had a harness
rather than because anyone had judged them low risk: `CallbackBackgroundService` (169 lines, 0%),
`ViewService` (72, 0%), `AuthenticationService` (48, 0%) and `AuthenticatingHandler` (36, 0%) - the
player.api and identity clients that every test substitutes. Covering them meant a substituted
`HttpMessageHandler`, which nothing in the suite had needed; they now sit at 98.2%, 100%, 89.5% and 100%,
and the four characterizations above are what came out of writing them.

## What the map says now

`ViewService`, `AuthenticatingHandler`, `VmHub` and `VmTeamDeletedSignalRHandler` have no unexecuted lines
or branches left at all. What the other three classes item 8 touched have left is eleven lines, each named
in a `<remarks>` as unreachable or as not worth the arrangement - which is the state to leave a class in
rather than chasing the last percent:
`AuthenticationService`'s `RenewToken` catch-all and the null it returns (five lines - the pair of tests
above is the argument that nothing can reach it), `CallbackBackgroundService`'s retry-delay cap (three
lines: the delay grows by five seconds an attempt and the ceiling is two minutes, so reaching it takes
twenty-four failures and about twenty-five minutes of real waiting), and `BulkPowerOperation`'s `catch
(EntityNotFoundException<Vm>)` in `TryCanAccessVm` (three lines - `CanAccessVm` raises that only for a
null Vm, and the handler passes it rows it has just loaded, so an id that matches nothing never gets
that far).

What the ranking says next, once `Domain.Vsphere` is set aside as reachable only through `IVimClient`, is
smaller and more scattered than what item 8 found: the untested remainder of `PlayerService` (70 lines of
73.4%), `EntityEventInterceptor` (63 of 66.4%), `ProxmoxIsoProvider` (39 of 71.9%),
`ActiveVirtualMachineService` (38 of 68%), `IsoService` (29 of 93.2%), `DatabaseExtensions` (25 of 55.3%)
and the two Swagger operation filters (17 each, 0%). Nothing in that list is a subject the way the clients
were - each is the residue of a class the suite already drives, which is what a coverage map looks like
once the classes nothing drives have been dealt with.

Item 10 changed what the top of that ranking means. `ProxmoxService` was the single largest untested class
in the repository after `VsphereService`; it is now at 99.4%, and its one uncovered line is
`ReplaceBridge`'s `bridge=` append at `:352`, which the tests prove is unreachable through its only
caller - the state to leave a class in rather than chasing the last percent. What the Proxmox namespace had
left after it was three classes, and one of them is the reason the other two were worth doing:
`ProxmoxTaskService` (130 lines, 0% at that point) and `ProxmoxStateService` (126, 0%) are the background
services, both reachable through their own substituted interfaces without a cluster, and
`ProxmoxIsoStorageService` (81 untested of 25%) is the ISO upload path, which shares the same
`IHttpClientFactory` seam the driver tests already use.

Item 11 took the last three background services with no tests off the zero line, and did it without moving
what the ranking is *about*: `TaskService` 0% → 100% of 213 lines, `MachineStateService` 0% → 100% of 131,
and `ProxmoxTaskService` 0% → 100% of 130. Branch coverage is 98.3%, 100% and 96.8%. All three left the
untested-lines ranking entirely, `Domain.Vsphere` went from 15.6% to 29.2% and `Domain.Proxmox` from 63.8%
to 77.2%, and the assembly from 65.3% to 71.0%. The four lines that survived the first full run of
`TaskServiceTests` were its per-task `catch`, which is now covered too - the only way in is a moref
translation that throws, which needed one line of harness rather than a new arrangement.

What that leaves at the top of the Vsphere namespace is unchanged in kind and shorter by three entries:
`VsphereService` (1,169 lines of 25.2%) and `VsphereConnection` (405 of 3.8%) are below `IVimClient` and
close to their ceiling, `ConnectionService` (180, 0%) is the connection cache and login loop, and
`VimExtensions` (96 of 6.7%) is the property-bag reader the pollers use and nothing tests directly.

Item 12 then took the fourth poller off the zero line with the harness item 11 had already built:
`ProxmoxStateService` 0% → **100%** of its 126 lines and 20 of 20 branches. `Domain.Proxmox` went from
77.2% to 90.2% (878 of 973 lines), the assembly from 71.0% to 72.5% line and 69% branch, and
`ProxmoxExtensions` came along with it to 80.3%, since `GetPowerState` is what the poller writes through.
What that leaves with the most untested lines in the Proxmox namespace is `ProxmoxIsoStorageService`
(81 untested of 25%), the ISO upload path - not a class nothing drives, since
`ProxmoxIsoStorageServiceTests` covers its statics, but the residue of one, which is the same kind of
entry as everything on item 9's list. It needs no cluster either: it shares the `IHttpClientFactory` seam
the driver tests already use.

One entry on the list is not a test target at all. `Player.Vm.Api.Hubs.VmHub` (18 lines, 0%) is
unreachable: `Startup` imports `Player.Vm.Api.Features.Vms.Hubs` and nothing anywhere else names the
`Player.Vm.Api.Hubs` namespace, so the `MapHub<VmHub>` in `Startup` is the feature hub and this copy has
no caller. The only reference to it in the repository is the assertion in `HubConnectionTests` that it is
not mapped. Deleting the file is the fix; the coverage figure is only how it was noticed.

# What is not covered yet

The suite is being grown in stages, and it is worth being explicit about what a green run does *not*
currently tell you.

- **Authorization at the edges of it.** `PlayerService`, `VmService` and `NetworkService` are driven down
  the refusing path as well as the permitting one, and every endpoint class covering an authenticated
  route asserts its 401 and at least one refusal - a 403, or in `BulkPowerOperationEndpointTests` the
  per-VM `"Unauthorized"` and `"Insufficient Permissions"` a bulk command reports instead, both of them now
  produced by a request rather than reasoned about. `ProxmoxController` and `VsphereController` now
  each have the full map of which permission every one of their routes asks for, including the ones that
  ask for none beyond team visibility. `VmUsageLoggingSession` has the same map, driven twice - denying
  the pair each route asks for, and denying the opposite pair to show the route still answers. The hubs
  are now in that set too, as far as there is anything to deny: `VmHub.JoinUser` is the only hub method
  that refuses a caller, and its refusals are covered; the rest compute what the caller can see rather
  than deciding yes or no, and `ProgressHub` decides nothing at all - characterized above rather than left
  as a gap.
- **The client's half of the hub contract.** Both hubs are now covered - the group names, the presence
  bookkeeping, the calls into the usage log and the writer behind them, and one round trip over a real
  connection - along with the five entity-event handlers that broadcast into `VmHub`'s groups, so both ends
  of every name the server uses are asserted. All eight controllers have endpoint tests, covering all 82
  actions: `VmController`
  (23), `VsphereController` (21), `ProxmoxController` (17), `VmUsageLoggingSessionController` (9,
  including CSV and report generation), `NetworksController` (5), `FileController` (4),
  `HealthCheckController` (2) and `CallbackController` (1). What no test in this repository sees is the
  Angular side: the method names it listens on and the group names it joins are asserted here as the
  strings *the server* uses, and nothing checks that the two agree. Nor do the two server-side ends compare
  themselves to each other - each asserts the same names independently, and a renamed group would have to be
  renamed in both suites - which is a gap a test cannot close without one of them stopping being a test of
  what the code does. One thing on the server side is still genuinely open: `VmHub` is not driven over a
  live connection, for the routing reason above.
- **The projections.** The AutoMapper profiles run in every endpoint test, but only the projections those
  tests happen to read are asserted.
- **What the out-of-process clients assume.** `ViewService`, `AuthenticationService`,
  `AuthenticatingHandler` and `CallbackBackgroundService` are now covered down to the socket, which is as
  far as this repository can go: everything above the transport is production code and everything below it
  is what the tests decided the other service would say. The routes and the DTOs are the generated
  `Player.Api.Client`'s, so those move with a package bump rather than silently - but which status code
  player.api answers for a team it does not have, that a refused grant carries an `error` field, and how the
  webhook payload spells its properties are all assumptions no test here can check. Two smaller things are
  also unasserted: how the `ActionBlock` orders events beyond the two-event case, and any retry past the
  first, since the delays are real seconds and the ceiling is twenty-four attempts away.
- **The hypervisor edge - vSphere permanently, Proxmox no longer.** No harness makes a vCenter or a
  Proxmox cluster available in CI, and this is still most of the untested code in the repository - 1,951
  lines between `Domain.Vsphere` at 29.2% and `Domain.Proxmox` at 90.2%, though 1,856 of those are now on
  the vSphere side alone. But the two halves are not the
  same kind of gap, and this section used to say they were. vSphere's client is reached only through
  `IVimClient`, so a substitute there is as far down as a test can go and `VsphereService` at 25.2% is
  close to the ceiling. Proxmox's is not: `ProxmoxService` constructs its `PveClient` from
  `IHttpClientFactory.CreateClient("proxmox")`, so the transport can be replaced instead of the client,
  and the driver is now at 99.4% with no cluster involved. What remains genuinely out of reach is
  narrower than "the hypervisor edge": whether the routes and payloads these tests assert are the ones a
  real PVE accepts, whether a real cluster ever omits the response keys the driver mishandles, and every
  vSphere call below `IVimClient`. The first two of those are what `crucible-tests` against a deployed
  environment is for; the third is not meant to move.

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
6. ~~The entity-event handlers that broadcast into `VmHub`'s groups, which are what is left of the
   application's own surface.~~ Done. `VmSignalRHandlerTests` and `VmTeamSignalRHandlerTests` drive the five
   handlers directly, and `EntityEventBroadcastTests` drives the path from a save through the interceptor and
   real MediatR to a broadcast, which is also the only test of the wiring itself. The group names are now
   asserted from both ends, and the argument shapes the client has to tolerate are written down.
7. ~~Coverage measurement, opt-in and ungated, purely as a map of where the untested risk still is.~~
   Done. `scripts/coverage.sh` locally and a hand-started `coverage.yml` on a runner, with no threshold
   anywhere and nothing collected by the build that gates a pull request. The Coverage section above has
   the first run's figures, what the exclusions are, and the four holes it named that reading the code
   had not.
8. ~~The holes the map named, which is the first list of work here that came from a measurement rather
   than from reading the code.~~ Done, and it is the item that changed the coverage figure most - lines
   from 55.3% to 59.3%, and `Domain.Services` from 48.3% to 83.9%. The four narrow holes first: the
   Proxmox half of the bulk power path and the two refusals beside it, the `vms/actions/power-off` route,
   `VmHub.JoinUser`'s active-Vm branch for a caller who is not a view admin, and
   `VmTeamDeletedSignalRHandler`'s suppression loop past a team in another view - sixteen cases added to
   three existing classes. Then the out-of-process clients, which needed a harness rather than a test:
   `Support/RoutedHttpHandler.cs` (since replaced by the shared `StubHttpMessageHandler`'s route rules), a
   substituted transport with the real generated client,
   IdentityModel and Polly above it, and 45 cases across four new classes -
   `ViewServiceTests`, `AuthenticationServiceTests`, `AuthenticatingHandlerTests` and
   `CallbackBackgroundServiceTests` - which took all four off the zero line together. Four
   characterizations came out of writing them, listed above; the sharpest is a 502 from player.api
   producing a set of maps nobody can see instead of a retry.
9. The residue, which is what the map has left to say. It is a different kind of list from item 8's -
   every entry is part of a class the suite already drives, so each is an arrangement that was never
   produced rather than a subject nobody has looked at: `PlayerService`'s untested remainder (70 lines),
   `EntityEventInterceptor` (63), `ProxmoxIsoProvider` (39), `ActiveVirtualMachineService` (38),
   `IsoService` (29), `DatabaseExtensions` (25) and the two Swagger operation filters (17 each). Worth
   doing in that order, worth stopping when the remaining lines are all named in a `<remarks>`, and worth
   doing after deleting `Player.Vm.Api/Hubs/VmHub.cs`, which is 18 unreachable lines that no test can
   cover and no caller can reach.
10. ~~The Proxmox driver at the socket.~~ Done, out of order and ahead of item 9, because it came from a
    question rather than from the map: which of the gaps above can be closed with no hypervisor at all?
    `ProxmoxService` turned out to be one of them. It constructs its own `PveClient` from
    `IHttpClientFactory.CreateClient("proxmox")`, so the seam is the transport, and item 8's
    `RoutedHttpHandler` was already most of a harness for it - it needed a method prefix on a rule pattern,
    because reading a VM's config and writing it are the same path and differ only in verb, and a lazily
    computed body, because a cluster gains machines and migrates them between two reads of one path.
    On top of that, `Support/FakeProxmoxCluster.cs`: one cluster holding whatever machines a test
    registers, arranged in Proxmox's own vocabulary of paths and JSON, with the real client's route
    building, `{"data": ...}` envelope, model binding and task waiting all still running. Then 236 cases
    across seven classes - `ProxmoxServiceCommandTests`, `…ConfigTests`, `…ConsoleTests`,
    `…GuestAgentTests`, `…IsoMountTests`, `…SnapshotTests` and `…VmLookupTests`, the last of these against
    real PostgreSQL because the node-refresh path reads the database. `ProxmoxService` went from 3.7% to
    99.4% of its lines, `Domain.Proxmox` from 9.0% to 63.8%, and the assembly from 59.3% to 65.3%.
    Ten characterizations came out of it, listed above; the sharpest is that eight `!= null` guards
    against a missing Proxmox response key are dead code, because reading an absent member of an
    `ExpandoObject` throws rather than answering null. What is left of the driver is the three classes
    beside the service - `ProxmoxIsoStorageService` past its statics, `ProxmoxPrimaryHandler`, and the
    unguarded named-client registrations in `ServiceCollectionExtensions` - none of which need a cluster
    either.
11. ~~The background pollers.~~ Done, still ahead of item 9 and for the same reason as item 10: they were
    the largest thing left that needs no hypervisor. Three services that had never been driven at all -
    vSphere's `TaskService` (213 lines), `MachineStateService` (131) and `ProxmoxTaskService` (130) - each
    an infinite `while` around a scope, a hypervisor query, some database writes and a SignalR broadcast.
    The problem was never the hypervisor, which `IVimClient` and item 10's `FakeProxmoxCluster` already
    answer for; it was the loop, which has no seam and no way to be asked for exactly one pass.
    `Support/PollLoop.cs` is that seam, and it is the item's real product: the test's own
    `IServiceProvider`, which counts passes because a pass is one `CreateScope`, and bounds them because
    a pass past its allowance throws into a `catch` the service already has. A refused pass is a barrier,
    so `Run(passes: n)` returning means pass *n* finished - writes and broadcasts included - without a
    sleep anywhere in the suite. Then 96 cases across four classes: `TaskServiceTests` (40),
    `ProxmoxTaskServiceTests` (31), `MachineStateServiceTests` (21) and `PollLoopSmokeTests` (4) for the
    harness itself. All three services went from 0% to **100%** of their lines, `Domain.Vsphere` from
    15.6% to 29.2%, `Domain.Proxmox` from 63.8% to 77.2%, and the assembly from 65.3% to 71.0%. Eleven
    characterizations came out of it, listed above; the sharpest is that `MachineStateService`'s
    loop-level catch logs at `Debug`, so the poller behind every power indicator in the UI can fail every
    pass for the life of a deployment and log nothing anybody reads. Two of the eleven were found by
    mutation rather than by reading - `_runningTasks.Clear()` and `_tasksPending = false`, one line each,
    both correct, neither asserted by any of 37 existing cases - which is the strongest evidence in this
    document for step 8 of "Adding a test". What is left above the two drivers is `ProxmoxStateService`
    (126 lines, 0%), which is the fourth poller and needs nothing this item did not already build, and
    `ConnectionService` (180, 0%), which is the vSphere connection cache and does need a vCenter.
    `MachineStateService` and its 21 cases have since been removed: vSphere power state now arrives
    through `VsphereMachineWatcher`'s change feed rather than an event poll, and `ConnectionService` is
    no longer at 0% - see `VsphereMachineWatcherTests` and `ConnectionServiceTests` above.
12. ~~The fourth poller.~~ Done, and it is the cheapest item on this list, because item 11's `PollLoop` was
    already most of a harness for it: `ProxmoxStateService` (126 lines, 0%) is the loop behind the Proxmox
    power indicator, and `ProxmoxStateServiceTests` took it to **100%** of its lines and 20 of 20 branches
    in 27 cases with no new infrastructure at all. What it did need was a decision about where the
    resources come from, and the answer is not the obvious one: `IsRunning`, `IsStopped` and `IsPaused` on
    a `ClusterResource` are plain settable booleans the client's deserializer fills from PVE's `status`
    field rather than properties computed from `Status`, so a hand-built resource reports
    `PowerState.Unknown` however its status reads, and a test that built its own would assert nothing about
    the chain that matters. Every resource in the class therefore comes out of item 10's
    `FakeProxmoxCluster` through the real `ProxmoxService`, with `IProxmoxService` substituted above that
    so a pass can still be made to meet an unreachable cluster or a vmid the cluster lists twice.
    `Domain.Proxmox` went from 77.2% to 90.2% and the assembly from 71.0% to 72.5%, and `ProxmoxExtensions`
    came along to 80.3%. Three characterizations came out of it, listed above; the sharpest is not about
    this service on its own but about the pair - this poller's `vm.Type = VmType.Proxmox` is the only thing
    that closes `ProxmoxTaskService`'s never-cleared-spinner trap door, so that trap door is dormant only
    while `Proxmox:Enabled` is true and only for a machine PVE still lists, and nothing in either file says
    so. One coverage hole was
    found by reasoning rather than by a run and is worth repeating as a pattern: removing the
    `if (pveVm != null)` guard from the private `UpdateVm` reddens nothing, because "the row was left
    alone" and "the pass threw on its way past" look identical from the row. The fix is to assert the
    absence of a logged failure alongside the unchanged row, which is now what every "nothing should have
    happened" test in the class does. Between them items 11 and 12 covered every `BackgroundService` in the
    repository that can be reached without a hypervisor; the one still at zero is `ConnectionService`
    (180 lines), which is the vSphere connection cache and does need a vCenter. The largest thing left in
    the Proxmox namespace is the untested remainder of `ProxmoxIsoStorageService` (81 lines of 25%), which
    needs no cluster either. Beyond that, item 9's list plus the four Swagger filters (54 lines, 0%) and
    `IdentityResolver` (10, 0%, a two-line wrapper over `IHttpContextAccessor` that nothing in the suite -
    and, on a grep of the repository, no caller either - reaches) is what the map has left to say.
