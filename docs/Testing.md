Player.Vm.Api has an automated test suite in the `src/Player.Vm.Api.Tests` project. This document
details how the suite is built, how to run it, the conventions to follow when adding to it, and -
because the suite is deliberately being grown in stages - what it does not cover yet.

# Testing

The suite contains 1,463 tests across 53 test classes. All of them run today; nothing is skipped.

It is built on xUnit v3, NSubstitute and Testcontainers, and needs nothing from the environment except
Docker: no network, no vCenter and no Proxmox cluster. The 560 unit tests need not even that.

Twenty-four of the fifty-three classes are isolated unit tests. They construct the thing under test
directly and substitute its collaborators. `VsphereIsoProviderTests` and `VsphereServiceCommandTests`
are the largest and most important of them: they drive `VsphereService` and its ISO provider through a
substituted `IVimClient`, which is the only seam between those and a live vCenter.

Six of them are the Proxmox driver, and they are substituted a layer lower than that. `ProxmoxService`
builds its own `PveClient` in its constructor out of `IHttpClientFactory.CreateClient("proxmox")`, so
the seam available is the socket rather than an interface, and `Infrastructure/FakeProxmoxCluster.cs` is
a cluster that answers requests: the client's route building, its `{"data": ...}` envelope, its typed
model binding and its task waiting all run for real, and a test asserts the request Proxmox would
actually have received. `ProxmoxServiceCommandTests` (power commands, the cluster-wide reads and the NIC
options), `ProxmoxServiceConsoleTests`, `ProxmoxServiceConfigTests`, `ProxmoxServiceIsoMountTests`,
`ProxmoxServiceSnapshotTests` and `ProxmoxServiceGuestAgentTests` divide it by method; the seventh
class, `ProxmoxServiceVmLookupTests`, is database-backed rather than isolated, because
`GetCurrentNodeForVm` and `BulkPowerOperation` are the only two of the interface's twenty-one members
that read one. That every other class passes a **null** `VmContext` is itself the assertion that the
rest do not.

Fourteen classes host the application in process and send real HTTP requests through it. Everything between
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
- `ContractTests` and `OpenApiSurfaceTests` are the two classes whose subject is not this application but
  its agreement with the clients that consume it. They host it because that is the only way to reach the
  two things they assert against: the hub endpoints the application actually maps, and the OpenAPI
  document it actually serves. Both are described in "The contract with the clients" below.

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
database without needing a host, against fourteen that need a host and twenty-four that need neither.
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

`Infrastructure/PollLoop.cs` is what makes one assertable, and reading it first is worth more than reading
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
floor and slept for nothing would still fail. `Infrastructure/PollLoopSmokeTests` tests the harness itself
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
- `MachineStateServiceTests` covers `MachineStateService`, which is not a task poller: it asks each vCenter
  for the power events since it last looked and writes what they imply onto `Vm.PowerState`. Its subject is
  as much the window as the mapping - where the first one starts, when it advances and when it must not -
  because a window that moves over an outage drops the events in it silently and the indicator is simply
  wrong from then on.

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
  tracker produced. Last are the two states a Vm arrives in - with its teams loaded, or without them, which
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
rather than an interface - a substituted `HttpMessageHandler`, `Infrastructure/TestHttpHandler.cs`,
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

`Infrastructure/DatabaseHarnessTests` tests the harness itself. Each of its assertions guards a
property the rest of the suite silently relies on and which would otherwise degrade without failing
anything: that the provider really is Npgsql, that every migration is applied, that snake_case casing
and store-generated UUIDs reached the schema, that foreign keys are enforced, that a test sees only its
own rows, that a request writes to the database of the test that made it, and that the usage log is a
second database of its own rather than the same one with more tables in it.

# The contract with the clients

Everything above tests what this application does. This section is about what it *agrees* with the
browsers that use it, which is a different thing and fails in a different way.

Two of the three channels between this API and its clients are agreed at build time by repositories that
never see each other, and a disagreement produces no error anywhere:

- **SignalR** dispatches by name *and* argument count. A client that invokes `JoinView` with two
  arguments against a one-argument hub method gets a rejected invocation on a connection that stays up -
  the view simply stops receiving updates. A client that registers a handler for a message name nothing
  sends is never called. `vm.ui` and `console.ui` each hold their own copy of every one of these strings.
- **The generated API client.** `vm.ui/src/app/generated/vm-api` is generated from this API's OpenAPI
  document by `npm run swagger:gen` and then *committed*. Nothing runs that on a schedule, in a pipeline,
  or as a condition of merging - so a DTO property renamed here changes the JSON this API sends and
  changes nothing about the TypeScript interface the browser parses it into. Both repositories build,
  both test suites pass, and the field is `undefined` in production.

The third channel, plain HTTP, is not in this category: a route that moves is a 404 somebody notices.

## What is written down

`contracts/` at the root of the repository holds two files, and `contracts/README.md` describes them for
a reader who arrives at the directory rather than at this document.

`contracts/signalr-contract.json` is **generated**. It lists both hubs with the path each is mapped at,
the invocations each declares with their argument counts, the messages each broadcasts with the argument
counts they go out with and the producers that send them, which clients consume each hub, and the
`modifiedProperties` names `VmUpdated` can carry. Everything structural in it is taken from the
application - the paths from the endpoints `MapHub` added, the invocations by reflection over the hub
classes, the broadcasts and their `sentBy` lists by driving the real producers - so nobody types a name
into it and nobody has to remember to.

Regeneration writes *into* the file rather than replacing it, because it is read by people on the other
side of the estate and most of what makes it worth reading is not derivable here. Four things survive a
regeneration untouched and are the parts to edit by hand: the `description` fields, the per-entry `note`
prose, the `clients` lists naming which Angular service talks to which hub, and
`clientListenersWithNoSender`. All four are facts about the browser clients or about why an entry is the
shape it is, and a repository that cannot see those clients cannot generate them.

The `progress` hub's `broadcasts` are the one structural exception, and the one thing in the file still
written by hand. `Progress` has no constant and no event handler behind it - both task pollers write the
literal - so driving it would mean standing up a whole poller harness for a fact `TaskServiceTests` and
`ProxmoxTaskServiceTests` already establish. `ContractTests` names the exception in
`HubsWhoseBroadcastsAreDriven` rather than leaving a reader to notice it, and keeps the entry honest in
`TheProgressBroadcast_IsTheLiteralBothPollersSend`.

`contracts/openapi-surface.json` is **generated** too, and is a derived summary rather than the document. The
document is 170KB and most of it is XML doc comments, so a snapshot of it reddens when a `<summary>` is
reworded - and a test that fails for reasons that do not matter gets regenerated without being read,
which is the failure mode that makes snapshot tests worthless. What is kept is what a generated client is
built out of: operation ids and tags, because they become method and service names; parameters, request
bodies and response types, because they become signatures; and schema properties with their types,
nullability and required flags, because they become interfaces.

Both files are read from the repository rather than from a copy in the test output, because the point of
them is that something outside this repository reads the same bytes. `Infrastructure/Contracts.cs` is the
one place that knows where they are, and it takes the path from an `AssemblyMetadata` item in
`Player.Vm.Api.Tests.csproj` rather than walking up from the test binary - a walk guesses at a directory
layout, and a copy in `bin/` would let a regeneration write somewhere git never sees. A missing directory
throws rather than skipping: a contract test that quietly passes when it cannot find its contract is
worth less than no test.

## What this repository asserts

`ContractTests` (13 cases) both generates the file and asserts it against the server. The generation is
one case, `TheContract_IsWhatTheApplicationGenerates`, which is the whole file at once; the others assert
the halves of it separately, because the halves are reachable separately and a failure that names the one
that moved is worth more than a diff of the file. Keeping both is also the only defence against a bug in
the generation itself: a lone generated snapshot compared against its own generator can only ever agree
with it, and would stay green against a file the focused cases fail.

- **The hubs.** The `EndpointDataSource` of the running application, filtered to endpoints carrying
  `HubMetadata`, must be exactly the hub types the contract names, each at the path its `RouteEndpoint`
  is mapped at. This is what stops the file describing a hub nobody can reach, or naming a path `MapHub`
  no longer uses - `HubConnectionTests` writes the two paths out as constants of its own and connects to
  them, which is a different question, and nothing but this compares a path in the file to where
  `MapHub` actually put it. The negotiate endpoint `MapHub` adds a segment below each hub is dropped: it
  belongs to the transport, and no client names it.
- **That every hub names a client.** The one field regeneration cannot fill in is `clients`, so a newly
  mapped hub arrives in the file with an empty list - and an empty list is what makes the entry inert,
  because `crucible-tests` generates its per-client checks by looping it. A hub with none is in the shared
  list, reads as covered, and is compared to nothing. It is checked here rather than there for two
  reasons: this repository's pipeline is the one that runs on the commit that emptied the list, and
  `crucible-tests` does not run the contract specs in CI at all. A hub that genuinely has no browser
  client goes in `HubsWithNoBrowserClient`, empty today - an exception written as a line of code, because
  an empty list in the file cannot be told apart from one nobody filled in.
- **The invocations**, per hub, by reflection over the hub class: the public declared instance methods
  that are not `OnConnectedAsync`/`OnDisconnectedAsync`, as `name/parameter-count` pairs, must be exactly
  what the contract lists. The count is in the set rather than checked separately, so a failure names the
  arity and the method together.
- **The return payloads.** `JoinViewUsers` and `JoinUser` answer the caller, and the client destructures
  what comes back. The keys are taken from the *host's own* `JsonOptions` - `JsonTypeInfo.Properties` for
  the declared return type, unwrapped through `Task<>` and through `IEnumerable<>` - so they are the keys
  the configured serializer will actually produce, not the property names of the CLR type.
- **The broadcasts**, by driving the real producers. All five entity-event handlers are constructed
  against a `HubContextHarness<VmHub>` and given a real saved `Vm`; `VmHub.SetActiveVirtualMachine` and
  `UnsetActiveVirtualMachine` are invoked against a `HubHarness`. What is asserted is the set of
  `name(arities) from producers` that came out. Reading `VmHubMethods`' constants would have been easier
  and would have asserted the wrong thing: a constant is what the server *has*, and the arity - the half
  SignalR dispatches on and no compiler checks - only exists at the call site. A separate case does read
  the constants, for the narrower question of whether any of them is missing from the contract entirely.
  Each drive is bracketed so the sends it produced are attributed to it, which is where the `sentBy`
  lists come from; they are worth deriving rather than annotating because a handler can be split or
  renamed without changing anything about what goes on the wire, so `sentBy` is the part of an entry
  most likely to go stale.
- **`modifiedProperties`**, from both ends. The names must be exactly the camel-cased scalar properties of
  the `Vm` entity as EF's model reports them, because that is what `TrackedEntityEntry.GetModifiedProperties`
  can ever return; and every one of them must be a JSON key of the `Vm` DTO, because `vm.ui` spends the
  list as `model[x] = vm[x]` and a name that is not a key writes `undefined` over a value that was correct
  a moment ago. The keys with no scalar behind them are recorded too, and asserted to be exactly the ones
  recorded - they are real keys that `modifiedProperties` can never name, and a client that only applies
  what it names will never see them move.

`OpenApiSurfaceTests` (3 cases) pins the surface. The document comes from the hosted application over
HTTP - `app.UseSwagger()` is unconditional, so the in-process host serves it - and one case asserts the
summary matches the checked-in snapshot. The second fetches the document twice and asserts the two
summaries agree, because a snapshot is worth nothing if the thing it snapshots is a dictionary iterated in
hash order. The third asserts no `$ref` in the document names a schema it does not define, which is worth
its own case because `ModelDocumentFilter` adds schemas by hand for types no controller signature
mentions - exactly the arrangement in which a rename leaves a reference behind.

## What `crucible-tests` asserts

Neither class above can see a client; this repository's CI runs alone. The other half is
`crucible-tests/playerVm/tests/contract/`, which can see every repository in the workspace and reads the
same two files. `signalr-contract.spec.ts` asserts, per hub and per client, that the client invokes only
methods the contract declares with the argument counts it declares, listens only for messages it says are
sent, binds no more arguments than the smallest arity a message is sent with, and dials the path it says
the hub is mapped at - plus, in the direction this repository cannot check, that every message the API
broadcasts is listened for by somebody. `openapi-surface.spec.ts` compares the pinned surface to the
committed client: the schema set against the generated models, each schema's properties against the
generated interface, each enum's values against the generated union, and every operation id against a
method on the service its tag names. 24 cases between them, needing nothing running.

That division is deliberate. This repository owns "the file is true of the server", which needs the
server; `crucible-tests` owns "the clients honour the file", which needs the clients. Neither half is
useful alone, and the file is what joins them.

## Regenerating the contracts

```bash
VMAPI_UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~ContractTests"
VMAPI_UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~OpenApiSurfaceTests"
```

Each rewrites its file and then **fails on purpose**. Regenerating is not a way of passing: a run with
the variable set must never be green, or a pipeline that inherited the variable would rewrite the file on
every build and the test would never say anything again. Read the diff and re-run without the variable -
and for the surface, regenerate `vm.ui`'s client in the same change, because a surface that moved without
the client moving with it is precisely the state this is here to prevent.

Both files share the protocol, in `Contracts.AssertMatchesOrRewrite`. Sharing it is not only about
duplication: the deliberate failure above is the load-bearing line, and one of two copies of it is one
that can be the copy that gets dropped.

The obvious objection to either file is that a file a test can rewrite is a file that agrees with
whatever the code currently does. That is true, and it is why the diff is the product rather than the
green run: regeneration is a two-command sequence a person drives after reading what moved, and the
generated bytes are what `crucible-tests` holds the *clients* to. The alternative that was tried first -
`signalr-contract.json` maintained by hand, with the tests only asserting - put the same strings in a
third place and asked a person to keep them right, which is the arrangement these tests exist to replace
everywhere else in the estate.

## What writing it down found

Six things, none of which any test in either repository had been in a position to notice:

- **`console.ui` listens for a message nothing sends.** Its notification service registers a `Complete`
  handler that clears the progress state, and no part of this application ever sends `Complete`. The
  state is cleared by the last `Progress` message instead. Recorded under
  `clientListenersWithNoSender` with a `Pending upstream:` note, and asserted from the client side, so the
  entry is deleted when the handler is - rather than left as documentation of something no longer true.
- **`ProgressHub.Leave` has no caller.** A console that navigates away drops the connection, and the
  group is cleaned up by the disconnect. The method is not dead - it is just never reached from the UI.
- **`ActiveVirtualMachine` is bound with one argument of the four it sends.** `vm.ui` binds all four;
  `console.ui` binds only `vmId`. That is legal - SignalR drops what a handler does not bind - and it is
  why the client-side arity assertion is one-sided.
- **`VmCreated` goes out with two different argument counts.** `VmCreatedSignalRHandler` sends
  `(vm, null)` because it shares `VmBaseSignalRHandler`'s send with `VmUpdated`; `VmTeamCreatedSignalRHandler`
  sends `(vm)`. So a client may bind one argument and no more, and one that bound two would see
  `undefined` for half the VMs it was told about.
- **Three keys of the `Vm` DTO can never appear in `modifiedProperties`.** `defaultUrl`, `proxmoxVmInfo`
  and `teamIds` have no scalar property of the `Vm` entity behind them, so EF's change tracker never
  reports them however they change. A client that applies only what `modifiedProperties` names will never
  see those three move on an update; it has to take them from the whole `Vm` the first argument carries.
- **`Progress` is the one broadcast name in the application that is not a constant.** Both pollers write
  the literal, at `Domain/Vsphere/Services/TaskService.cs` and `Domain/Proxmox/Services/ProxmoxTaskService.cs`.
  It is also the one name `ContractTests` pins against a literal of its own rather than by driving a
  producer, which the class says in a `<remarks>` along with what does cover the sending side -
  `TaskServiceTests` and `ProxmoxTaskServiceTests`.

# Running the tests

```bash
dotnet test
```

**Docker must be running.** PostgreSQL is the only database these tests use, and there is deliberately
no in-memory or SQLite fallback - a fallback that quietly swaps the provider reports a green run that
never touched what production uses. Without Docker the 903 database tests fail, each naming the reason;
the other 560 still pass, because the container is started by the first test that asks for a database
rather than at assembly load.

A single class or a single test can be run with a filter:

```bash
dotnet test --filter "FullyQualifiedName~VsphereServiceCommandTests"
dotnet test --filter "FullyQualifiedName~VsphereServiceCommandTests.PowerOn_WhenAlreadyOn_ReportsItAndSendsNothing"
```

A full run takes about twenty seconds, container start and both sets of migrations included. About five of
those are one test: `CallbackBackgroundServiceTests.WhenTheFirstAttemptFails_TheEventIsKeptAndRetried` waits
out the real first retry delay, which is the shortest one the service has.

A plain run collects no coverage. `scripts/coverage.sh` is the opt-in way to get it, and nothing gates
on the figure it produces - see Coverage below for what it is for and why it has no threshold.

A plain run also asserts the contract files under `contracts/` and never writes them. The two commands
that write them are in "Regenerating the contracts" above, and both fail deliberately.

# Build settings

`Directory.Build.props` sets `TreatWarningsAsErrors` for every project, which is what enforces the
xUnit analyzers that ship with `xunit.v3`. These fail the build:

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
  `CallbackBackgroundServiceTests` is what covers the real one, outside the host, with a scope factory of
  its own - which is the same problem solved the only way it can be rather than avoided.
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

`Infrastructure/HubContextHarness.cs` is the other end of the same idea: what something *outside* a hub
broadcasts through an `IHubContext<THub>`, which is how the entity-event handlers and the task pollers reach
a group. It records the group name, method and arguments of every send and exposes them three ways -
`Sends` in order, `Of(method)`, and `Recipients(method)` deduplicated - because "which groups were told" and
"how many messages were sent" are different questions and both have tests. `Clients.All` is recorded under
a sentinel group name rather than in a list of its own, since a fallback that tells everyone is an answer to
who was told, and `VmDeletedSignalRHandler` has one.

`Infrastructure/TestHttpHandler.cs` is the seam under the out-of-process clients, and the only place in
the suite where what is replaced is the socket rather than an interface. Everything above it is production
code - the generated `PlayerApiClient`, IdentityModel's token request, the `HttpClient` pipeline and
whatever `DelegatingHandler`s are wrapped around it - which is the whole reason those tests are worth
having: a route the client builds, a name its deserializer looks for and a status code its error handling
keys off are all things this repository consumes rather than declares. Rules are matched by path in the
order they were added, a rule can be one-shot so that a later one answers the retry, `Throws` fails the way
a refused connection fails rather than with a status code, and every request is recorded with its method,
path, query, `Authorization` header and body. A request nothing stubbed **throws**, naming the path asked
for and every path that is stubbed: a 404 would be swallowed by the very error handling several of these
tests are about, so an arrangement that has drifted from the route the client builds has to fail loudly.
Bodies are serialized with System.Text.Json and no options, because the `Player.Api.Client` types carry
`[JsonPropertyName]` on every property and so serializing one produces the names its own deserializer
looks for; hand-written JSON is used only where the payload is not a type this repository has - the OAuth
token response, and the webhook payloads player.api sends as a string.

`Infrastructure/Contracts.cs` is not a substitute for anything - it is the loader for the two files under
`contracts/`, and the only thing in the suite that reads from the repository rather than from the test
output. The path comes from an `AssemblyMetadata` item in the test project rather than from a walk up from
the test binary, and a missing directory throws instead of skipping. "The contract with the clients" above
is what those files are for.

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
   mapping, its authorization, a round trip - is worth a live connection. For an entity-event handler,
   construct it over `Db` with a `HubContextHarness` and call `Handle` with the notification a real save
   published - `DatabaseTestBase.Mediator` has recorded them - rather than with one written by hand, so
   that what EF says changed is what the handler is given. For anything whose subject is a call *out* of
   this process, put `TestHttpHandler` under the real client instead of substituting the client: what is
   worth asserting there is the request that went out, and a substitute at the interface asserts only that
   the test and the production code agree about a method signature.
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

   A test whose subject is a *file* is mutated on both sides. `ContractTests` and `OpenApiSurfaceTests`
   were verified with eleven mutations - nine to `contracts/signalr-contract.json` and two to the hub
   classes - and the client-side specs in `crucible-tests` with thirteen more, to both Angular services,
   the committed client and the contract files. A contract test that only ever mutates the code is a test
   of the code; the mutation that matters is the one that makes the *file* wrong, because that is the
   direction a stale contract actually drifts. Both mutations of application source used for this have to
   compile: changing `ProgressHub.Join(string)` to take two parameters broke twelve existing callers, so
   the build failed, the run used a stale binary and reported the *previous* mutation's failures - adding a
   new `JoinAll()` method instead reddened exactly the intended case. Check that a mutation run actually
   rebuilt before reading its result.

9. If the change touches a hub method, a broadcast, or anything a client can see over HTTP, regenerate
   `contracts/` in the same change. A hub method renamed or given another parameter, a new broadcast, a
   broadcast sent with a different number of arguments, or a new scalar property on the `Vm` entity all
   redden `ContractTests` - and each of them means a matching change in `vm.ui` or `console.ui`, which is
   the point. Both files have a regeneration command in "The contract with the clients"; run it, read the
   diff rather than the green run, and regenerate `vm.ui`'s committed client alongside the surface.

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

The entity-event handlers add three more, and the last is the reason the group names are asserted directly
rather than through a request:

- `EntityEventBroadcastTests.CreatingAVmOnATeam_AnnouncesItTwiceToEachGroup`. Creating a Vm writes the Vm
  and its team rows in one save, so two handlers announce it: `VmCreatedSignalRHandler` sends `VmCreated`
  with the Vm and a modified-property list, and `VmTeamCreatedSignalRHandler` sends `VmCreated` with the Vm
  alone. Every group hears the same create twice, in two argument shapes. Harmless at the client - the
  second message carries the same Vm, and SignalR passes a missing argument as the parameter's default -
  but neither handler can easily know about the other, since each is told only about its own row.
- `VmSignalRHandlerTests.Created_ForAVmOnNoTeam_TellsNobody`, read against
  `Deleted_ForAVmWhoseTeamsWereNeverLoaded_TellsEveryone`. Two opposite answers to the same absence: with
  no teams to compute a group from, a create or an update reaches nobody at all, and a delete falls back to
  `Clients.All`. Both are defensible on their own - nobody can have joined a group for a team the Vm is not
  on, and a delete carries only an id - and the comment on the fallback is the only place either is written
  down.
- `EntityEventBroadcastTests.WhenAHandlerThrows_TheSaveStillSucceedsAndNobodyIsTold`.
  `VmContext.PublishEventsAsync` catches and logs each event's exception, so a handler that fails leaves the
  row written, the request answered and the clients never told. What a user sees is a Vm list quietly out of
  date until the page is reloaded, and `IViewService` calling player.api over HTTP is a realistic way to get
  there. Nothing else in the suite would notice, which is why these classes assert group by group rather
  than that a message was sent.

Smaller ones are `<remarks>`ed in place: the Vm in every broadcast carrying the id of every team it is on,
whichever group the message went to; the two team handlers loading their Vm with a `FirstOrDefaultAsync()`
that takes no cancellation token; and a team removal whose Vm has already gone telling the team but not the
view.

The out-of-process clients add four, and they are the kind that only a test at the socket can find - each
one is a decision the interface these classes sit behind does not express:

- `CallbackBackgroundServiceTests.ViewCreated_WhenPlayerFails_ClonesTheMapsWithNoTeamsAndDiscardsTheEvent`.
  `CloneMaps` catches `ApiException`, returns on a 404 and swallows everything else - then carries on into
  the loop that builds the clones with both team sets empty. So a 502 from a restarting player.api does not
  become a retry, which the surrounding machinery exists for and which would fix it; it becomes a set of
  maps nobody can see and an administrator has to reassign by hand, indistinguishable from a parent team
  that genuinely has no namesake in the child. Rethrowing anything that is not a 404 is a one-line change.
- `AuthenticationServiceTests.GetToken_ForATokenNotOutlivingTheThreshold_AsksEveryTime`, read against
  `GetToken_NeverRenewsATokenItStillHolds`. `ValidateToken` compares the lifetime the provider *stated*
  against `TokenRefreshSeconds`, which is not a countdown, so the comparison has the same answer for the
  life of the process: a deployment either renews on every single call - a full token request in front of
  every outgoing call to player.api, serialized on one lock - or never renews at all. What makes the second
  safe is `AuthenticatingHandler` treating a 401 as "get another one and retry", and nothing reports the
  first as anything but latency.
- `AuthenticatingHandlerTests.SendAsync_WhenNoTokenCanBeHad_ThrowsRatherThanSending`. `Authenticate`
  dereferences the token response for `IsError` without checking it for null, so a null one fails every
  outgoing request with a `NullReferenceException` naming nothing to do with authentication. Its only
  possible producer is `AuthenticationService.RenewToken`'s catch-all, and
  `AuthenticationServiceTests.GetToken_WhenTheProviderCannotBeReached_ReturnsAnErrorResponseAndNotNull` is
  the evidence that IdentityModel turns a transport failure into an error response instead - so the pair of
  tests is what says this is unreachable today and one refactor of either class away from not being.
- `ViewServiceTests.GetInfoForTeams_ForATeamPlayerDoesNotHave_ReturnsAnEmptyEntry`. A team player.api does
  not have still produces a `TeamInfo`, with a null view id, because the list is deduplicated by view id
  and nothing else has claimed null - and the method's only caller,
  `ActiveVirtualMachineService.SetViewActiveConsolesTelemetry`, casts that to `Guid` unchecked. Reaching it
  needs a console open on a team player.api has forgotten, which is why nothing has noticed;
  `GetViewIdsForTeams_SkipsATeamPlayerDoesNotHave` is the same input through the method that does check.

Smaller ones are `<remarks>`ed in place: the fifteen-minute sliding cache in `ViewService` holding the
*absence* of a team as firmly as its presence, so a Vm added to a team of a view still being created stays
out of that view's group until the entry lapses; `GetInfoForTeams` returning one entry per view while its
name and its `TeamName` field promise one per team; a 404 being forgiven on a team lookup but not on a
view's teams; the five retry waits of a shipped deployment holding a caller for a minute before it gets the
401 it was always going to get, with Polly's delays taking no cancellation token; and the webhook payloads
being parsed with Newtonsoft while the DTOs are annotated for System.Text.Json, which works only because
the names match case-insensitively.

The Proxmox driver adds the largest cluster of all, which is what 907 lines that had never had a request
made of them looks like. The first two are the ones to read before touching that file:

- **The `!= null` guards on a Proxmox response do not guard anything.** `PveClient` surfaces bodies as
  `ExpandoObject`, and dynamic member access to an *absent* key throws `RuntimeBinderException` rather
  than returning null - so in `data.exited != null ? (int)data.exited : 0` the exception is thrown while
  evaluating the condition and the fallback arm is unreachable code. Eight sites share the mistake:
  `exited` and `exitcode` in `RunGuestProcess`, `pid` in both guest-process methods, `content` in
  `ReadGuestFile`, and `description`, `parent`, `vmstate` and `snaptime` in `GetSnapshots`. The
  consequence is worst in `GetSnapshots`, because PVE appends a synthetic `current` entry to any
  non-empty snapshot list and that entry carries no `description`: a VM with snapshots gets a binder
  error naming `ExpandoObject` where the method means to return a list. The deadness was proved by
  mutation rather than inferred - changing both fallback literals in `RunGuestProcess` at once reddens
  *nothing*, which is normally a coverage hole and here is the finding. `DecodeAgentOutput` is the one
  response path that is safe, and only by accident: hyphenated keys like `out-data` cannot be reached
  dynamically at all, which forced it onto `IDictionary.TryGetValue`. What no test here can settle is how
  often a real cluster omits one of these keys - with `exited: 0` the code never reads `exitcode` - so
  the proven part is that the guards cannot work, not how often they are reached.
- **`BulkPowerOperation` reports a failed power operation as a success.** The per-VM dictionary uses
  `string.Empty` for "this one worked" and `result.GetError()` for a refusal, and `GetError()` is built
  only from an `errors` object in the body - so a bare 500 yields an empty string, indistinguishable
  from success. An unreachable node is worse: `PveClient` catches the transport exception itself and
  returns an unsuccessful `Result` with no `errors` object, so the per-VM `catch` never runs (the
  stale-node retry still does, which is the proof) and a whole node being down is invisible to the UI.
  The corollary is that the only exception which reaches that `catch` in practice is the
  `NotSupportedException` for `Revert`. Pinned in
  `ProxmoxServiceVmLookupTests.BulkPowerOperation_WhenAVmsNodeIsUnreachable_ReportsItAsSuccessButStillSubmitsTheRest`.
- **`GetCurrentNodeForVm` does not refresh the stored node**, though the interface doc comment says it
  does. `ResolveNode` assigns `info.Node` in memory, the query is `AsNoTracking`, and nothing calls
  `SaveChanges` on any path - so after a migration the call returns the new node while the row still
  holds the old one. Asserted against a fresh `DbContext`, which is the only way to see it.
- **`GetConsole` throws its refusals with no message.** `throw new Exception(result.GetError())` at
  `ProxmoxService.cs:175` discards the status code, the reason phrase and the route, so a rejected API
  token surfaces as an empty exception. Every other refusal in the file interpolates `GetError()` into
  context naming the operation and the vmid, which is why those tests assert with `Assert.StartsWith`.

Smaller ones are `<remarks>`ed in place: `ReplaceBridge`'s `bridge=` append branch being unreachable
through its only caller, since `ChangeNetwork` refuses an adapter whose bridge is blank and it is blank
exactly when there is no token to replace - it is also the only line of `ProxmoxService` that no test
covers; a config key like `netx` reaching `int.Parse` in `ChangeNetwork` as an unhandled
`FormatException`, which no real PVE config produces; `RunGuestProcessFast` reporting a pid as `long`
while `RunGuestProcess` narrows it to `int` for the status query, so pid `4294967303` is polled as
`?pid=7`, unreachable on Linux; `EnsureQemu` refusing a container with a 500 where `MountIso` refuses the
same impossibility with a 400; `GetError()` rendering an `errors` object as `"field : message"`, so a
Proxmox power refusal - which carries no field - reaches the UI with a leading `" : "`; and the two
`UrlEncode` families in use disagreeing on case, `WebUtility` in the console URL emitting upper-case hex
where `HttpUtility` in the query path emits lower-case and `+` for a space.

The four pollers add a cluster of their own, and it divides in a way the others do not: two of them are
about an operator never being told, and the rest are about a user seeing something wrong. The first is the
one to read before anything else in `MachineStateService`:

- **`MachineStateService`'s loop-level catch logs at `Debug`.** No deployment runs at Debug, so this poller
  failing every pass is silent: the power indicator stops following anything, every machine in the UI keeps
  whatever state it last had, and nothing is logged at a level anybody sees. The same class logs the
  *smaller*, per-connection failure at `Error` nine lines below, and the other three pollers use `Error` at
  their loop level, so it is a slip rather than a decision. Pinned in
  `WhenAWholePassFails_ItIsSwallowedAndLoggedAtDebugWhereNothingWillSeeIt`.
- **`HealthAllowanceSeconds` has no default and is compared with a strict `<`**, so zero means
  "unresponsive however recently it ran" - and vSphere's `TaskService` is the only thing that ever writes
  either half of that readiness check. It overwrites the class's own field default of 90 on every pass, so a
  deployment that sets any other `Vsphere` option without this one fails readiness forever while the poller
  works perfectly. An environment-variable install cannot inherit a single key from `appsettings.json` once
  it overrides the section, which is how that happens. `appsettings.json` ships 180. Pinned in
  `WithNoAllowanceConfigured_ReadinessFailsThoughThePassSucceeded`.
- **The power indicator is only correct from startup onward.** The first window per vCenter is seeded from
  the clock (`_lastCheckedTimes.GetOrAdd(connection.Address, DateTime.UtcNow)`), and `_lastCheckedTimes` is
  a field of a singleton `BackgroundService`, so "first pass" is once per process rather than once per
  reconnect. A machine powered off while the API was restarting still reads as on until something else
  changes its state. Pinned in
  `TheFirstWindowBeginsAtStartup_SoAPowerEventFromBeforeTheApiStartedIsNeverSeen`.
- **A window is not advanced when `GetEvents` fails**, which is the good half of the same design and is
  pinned for that reason: the `catch` returns before the assignment, so an outage does not consume the
  window and the events are re-requested next pass. Tidying the assignment above the `try` would silently
  drop every power event in the outage.
  `WhenAVcenterCannotBeReached_ItIsLoggedAndItsWindowIsNotAdvanced` is the test that would object.
- **`ProxmoxTaskService`'s query that *sets* `HasPendingTasks` has no provider filter, while the query that
  *clears* it does.** So a Vm with a `ProxmoxVmInfo` row and some other `Vm.Type` can be flagged by this
  poller and then skipped by its own clearing sweep forever - a spinner that never stops and power buttons
  that never come back. A trap door rather than a live bug: nothing in the application writes a
  `ProxmoxVmInfo` for a Vm it did not also type as Proxmox, so it needs a mis-migration or hand-edited
  data. The fix is the same `Type == VmType.Proxmox` clause on the second query. Pinned in
  `AVmWithProxmoxInfoButAnotherType_IsFlaggedAndThenNeverCleared`.
- **`ProxmoxStateService` is what closes that trap door, and only under two conditions neither file
  states.** Its pass selects on `ProxmoxVmInfo != null` - the same set as the flagging query, not the
  clearing one - and assigns `vm.Type = VmType.Proxmox` for every row the cluster still lists, so the
  mis-typed row above is retyped within one interval and the clearing sweep can then see it. That is the
  whole reason the trap door is a trap door rather than a live bug. It stops working if `Proxmox:Enabled` is
  false, which is the shipped default, and it never applies to a row PVE has stopped listing, because the
  retype lives inside the same `if (pveVm != null)` as the power state. Pinned from this side in
  `AVmWithProxmoxInfoButAnotherType_IsRetypedAsProxmox`, and the two tests are each other's other half.
- **A machine the cluster stops reporting keeps its state; a machine it reports in a state the client does
  not model loses it.** `ProcessVms` looks each row up with `TryGetValue` and passes the miss straight to
  `UpdateVm`, whose `if (pveVm != null)` leaves the row alone - so a machine deleted on PVE, or an entire
  cluster answering an empty list, freezes every indicator at whatever it last was, with nothing logged
  above `Debug` to distinguish it from an idle pass. But a machine PVE *does* list with a status the
  client has no flag for - anything that is not `running`, `stopped` or `paused` - is written as
  `PowerState.Unknown` and loses the state it had. The asymmetry is the finding: absence is treated as
  "no news" and an unrecognized presence as "no state". Pinned in
  `AMachineTheClusterNoLongerReports_KeepsTheStateItLastHad` and
  `AMachineTheClusterCallsUnknown_LosesTheStateItHad`.
- **`StateRefreshIntervalSeconds` has no property default**, unlike every other interval on
  `ProxmoxOptions`, so a deployment that overrides the `Proxmox` section without that key binds 0 and gets
  the floor - a one-second reconciliation of the whole cluster, which is the busiest this poller can be
  rather than the "off" an operator might read 0 as. `appsettings.json` ships 5. It is the same shape as
  `HealthAllowanceSeconds` above and reached the same way, an environment-variable install inheriting no
  single key from the file it overrode; the difference is that this one warns, once per distinct bad value.
  Pinned in `WithNoIntervalConfigured_ItFloorsAtOneSecondAndWarnsOnce`, with
  `AnIntervalCorrectedAndThenBrokenAgain_IsWarnedAboutAgain` for the once-per-*value* part.
- **vSphere's `TaskService` is the one poller whose `WaitAsync` is given no cancellation token.** Cancelling
  leaves it asleep for up to a full `CheckTaskProgressIntervalMilliseconds` - five seconds as shipped - so
  every restart and every rolling update waits that out, after which the container is killed rather than
  stopped if the orchestrator's grace period is shorter. `ProxmoxTaskService` and `MachineStateService` both
  pass one. It is also why `PollLoop.Stop` has to nudge after cancelling, and so why every test in that
  class depends on the defect being there - the `<remarks>` says which assertion replaces it once the token
  is passed. Pinned in `WhenCancelled_TheLoopSleepsOnUntilSomethingNudgesIt`.
- **A task that cannot be processed costs its own machine's spinner.** The per-task `catch` isolates the
  rest of the list, which is what it is for, but a task missing from `stillPendingVmIds` is
  indistinguishable from a task that finished - so the machine whose task could not be read is cleared while
  vCenter is still working on it. Pinned in
  `ATaskThatCannotBeProcessed_IsLoggedAndTheRestOfTheListStillIs`, which asserts both halves.
- **One hub, two vocabularies.** Both pollers broadcast the same `Notification` type to the same hub, and
  they do not agree on it. vSphere sends `queued`/`running`/`success` with a real `info.progress`; Proxmox
  sends `running` or PVE's own status string with `progress` **permanently the empty string**, because
  PVE's cluster task list carries no percentage. A client rendering a progress bar off that field shows one
  for a vSphere machine and nothing for a Proxmox one. Pinned from both sides.
- **The newest-event rule is per vCenter only.** The `GroupBy`/`OrderByDescending` that picks the latest
  event for a machine runs inside the per-connection loop, and the cross-connection merge is
  `eventDict.TryAdd` - so where two vCenters both resolve to the same Player Vm, the first connection in
  `GetAllConnections()` order wins outright however much older its event is. Narrow to reach - a machine
  moved between vCenters while both connection caches still hold its moref - and deterministic when it is.
  Pinned in `WhenTwoVcentersBothNameTheSameVm_TheFirstConnectionWinsRatherThanTheNewerEvent`.

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

Smaller ones are `<remarks>`ed in place: `AsyncExExtensions.WaitAsync` disposing neither its timeout CTS
nor its linked CTS, so every pass of all four pollers leaves a callback registered on the long-lived
stopping token; three of the eight `Task` properties vSphere is asked for never being read - `info.name`,
`info.cancelled` and `info.error` - so a cancelled or failed task is only ever "not queued and not
running" to that poller and a user is told a task ended, never that it failed; `_tasksPending` being set
from a task's state before the code asks whether a Player Vm was resolved, so another tenant's long
datastore operation holds this deployment's poller at its fast interval; `MachineStateService`'s
`endTimeSpecified` left false, so consecutive windows overlap rather than abut and an event can be
delivered twice - harmless, since writing a state twice equals writing it once; its first two passes asking
for the same instant, because the stored time is read one statement before its replacement is captured, so
the first window that has genuinely moved is the third pass's; there being no machine-state interval at all,
so slowing task-progress polling to spare a busy vCenter also slows how fast the power indicator notices
anything; `ProxmoxTaskService`'s per-task `catch` logging `task?.UniqueTaskId`, written for a null task
that would already have been dereferenced two lines earlier; and `Include(x => x.VmTeams)` on
`MachineStateService`'s update query, which nothing in that method reads - unlike the two pollers' own
`Include`s, which the entity-event handlers need in order to compute group names after the save;
`ProxmoxStateService`'s `DistinctBy(x => x.VmId)`, which is load-bearing rather than tidy, because PVE
lists a machine mid-migration under both nodes and the `ToDictionary` behind it would otherwise throw the
whole pass away - it keeps the first entry, so which node gets written for a migrating machine is PVE's
list order and nothing else; the same service's `Proxmox:Enabled` being read inside the loop rather than
around it, so turning Proxmox off does not stop the poller, it only makes every pass an empty one that
still wakes on the interval - and turning it on needs no restart; and its `UpdateVm` entry point, which
the hub and the command handlers use to write one machine without waiting for a pass, taking no
cancellation token, resolving its row with a `FirstOrDefaultAsync` that takes none either, and running on
an `ActionBlock` with `MaxDegreeOfParallelism = -1`, so a burst of console opens is a scope and a
`DbContext` per machine with no ceiling.

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

`coverage.yml` is the other workflow that runs the suite, and it is `workflow_dispatch` only: nothing
it produces is visible to any other run, and it cannot fail a pull request. The next section is what it
is for. Its NuGet cache key is the test job's plus `.config/dotnet-tools.json`, which shares the same
`restore-keys` prefix, so whichever of the two runs first warms the cache for the other.

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
and that workflow ask: `dotnet test` neither instruments nor slows down, and `test.yml` never
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

`coverlet.runsettings` holds the settings, and they matter to any figure quoted from it. Only
`Player.Vm.Api` is instrumented; EF's migrations are excluded by namespace and by file, because the
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
and the two Swagger operation filters (17 each, 0% at the time - item 13 took them off the list without
setting out to). Nothing in that list is a subject the way the clients
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

Item 13 moved the figure by accident, which is worth recording because it is the only entry on this list
that did. The contract tests were written for a reason that has nothing to do with coverage, but
`OpenApiSurfaceTests` fetches `/swagger/v1/swagger.json` from the hosted application - and generating that
document runs every Swashbuckle filter this API registers. The four of them were 54 lines at 0%, named on
item 9's list and again at the end of item 12 as work still to do; they are now 50 of those 54 lines, with
`DefaultResponseOperationFilter`, `JsonIgnoreQueryOperationFilter` and `ModelDocumentFilter` complete and
`JsonIgnoreFormDataOperationFilter` at 13 of 17 lines and 10 of 14 branches. The assembly went from 72.5%
to 73.1% line and 69.0% to 69.5% branch on 14 new cases. It is a reminder that a coverage figure measures
what was executed rather than what was asserted: nothing in either contract class asserts anything about
those filters, and the four uncovered lines that remain are the only honest signal in the change.

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
- **The client's half of the hub contract - the method names now, the group names still not.** Both hubs are now covered - the group names, the presence
  bookkeeping, the calls into the usage log and the writer behind them, and one round trip over a real
  connection - along with the five entity-event handlers that broadcast into `VmHub`'s groups, so both ends
  of every name the server uses are asserted. All eight controllers have endpoint tests, covering all 82
  actions: `VmController`
  (23), `VsphereController` (21), `ProxmoxController` (17), `VmUsageLoggingSessionController` (9,
  including CSV and report generation), `NetworksController` (5), `FileController` (4),
  `HealthCheckController` (2) and `CallbackController` (1). The Angular side used to be entirely unseen -
  the method names it listens on were asserted here as the strings *the server* uses and nothing checked
  that the two agree - and that is what `contracts/signalr-contract.json` and the section above now close:
  the method names, their argument counts, the message names and their arities are written down once and
  asserted from this repository against the server and from `crucible-tests` against both clients. What is
  still not compared is the *group* names. Those are computed twice inside this application - once when a
  client joins and once when something changes - and each end asserts them independently, so a renamed
  group would have to be renamed in both suites. That is a gap a test cannot close without one of the two
  stopping being a test of what the code does; a client cannot see a group name at all, so there is no
  third party to arbitrate. One thing on the server side is also still genuinely open: `VmHub` is not
  driven over a live connection, for the routing reason above.
- **When the contract checks actually run.** The two halves of the contract are asserted in two
  repositories, and only one of them gates a change to this one. `dotnet test` here fails if either file
  stops being what the application generates - a hub method added, a path moved, a broadcast's arity
  changed, the OpenAPI surface moved - and that much is a merge gate. Whether `vm.ui`'s committed client and the two Angular services still honour
  the same file is asserted only by `crucible-tests`, which is a separate repository with its own
  pipeline and is not run as a condition of merging here. So the order of discovery is: a change to this
  API that breaks a client reddens *this* suite first, at the snapshot, and the client-side spec confirms
  what broke afterwards. A change made only in a client - a renamed handler, a regenerated model - is
  caught by `crucible-tests` and by nothing here.
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
   `Infrastructure/TestHttpHandler.cs`, a substituted transport with the real generated client,
   IdentityModel and Polly above it, and 45 cases across four new classes -
   `ViewServiceTests`, `AuthenticationServiceTests`, `AuthenticatingHandlerTests` and
   `CallbackBackgroundServiceTests` - which took all four off the zero line together. Four
   characterizations came out of writing them, listed above; the sharpest is a 502 from player.api
   producing a set of maps nobody can see instead of a retry.
9. The residue, which is what the map has left to say. It is a different kind of list from item 8's -
   every entry is part of a class the suite already drives, so each is an arrangement that was never
   produced rather than a subject nobody has looked at: `PlayerService`'s untested remainder (70 lines),
   `EntityEventInterceptor` (63), `ProxmoxIsoProvider` (39), `ActiveVirtualMachineService` (38),
   `IsoService` (29), `DatabaseExtensions` (25) and the two Swagger operation filters (17 each - since
   executed, though not asserted, by item 13). Worth
   doing in that order, worth stopping when the remaining lines are all named in a `<remarks>`, and worth
   doing after deleting `Player.Vm.Api/Hubs/VmHub.cs`, which is 18 unreachable lines that no test can
   cover and no caller can reach.
10. ~~The Proxmox driver at the socket.~~ Done, out of order and ahead of item 9, because it came from a
    question rather than from the map: which of the gaps above can be closed with no hypervisor at all?
    `ProxmoxService` turned out to be one of them. It constructs its own `PveClient` from
    `IHttpClientFactory.CreateClient("proxmox")`, so the seam is the transport, and item 8's
    `TestHttpHandler` was already most of a harness for it - it needed a method prefix on a rule pattern,
    because reading a VM's config and writing it are the same path and differ only in verb, and a lazily
    computed body, because a cluster gains machines and migrates them between two reads of one path.
    On top of that, `Infrastructure/FakeProxmoxCluster.cs`: one cluster holding whatever machines a test
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
    `Infrastructure/PollLoop.cs` is that seam, and it is the item's real product: the test's own
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
13. ~~The contract with the clients: a generated-client freshness check, and one list of hub method and
    message names asserted from both repositories.~~ Done, and it is the first item on this list that did
    not come from the coverage map at all - it came from asking what a green run on both sides can still
    be wrong about. The answer was two whole channels. SignalR dispatches by name *and* argument count, and
    every one of those strings is written twice in the estate, once here and once in `vm.ui` or
    `console.ui`; the OpenAPI client `vm.ui` ships is generated by a command nothing runs and committed by
    hand, so a renamed DTO property is `undefined` in a browser and green in both suites. `contracts/`
    is the product: `signalr-contract.json` and `openapi-surface.json`, both generated, both regenerated
    only by a person reading a diff. `signalr-contract.json` started out hand-maintained and deliberately
    without a regeneration path, on the reasoning that a file a test can rewrite agrees with whatever the
    code currently does; that was the wrong trade. It put the same strings in a third place and asked a
    person to keep them right, which is the arrangement these two classes exist to replace everywhere
    else. Everything structural in it is now derived - paths from the mapped endpoints, `sentBy` from
    which producer each observed send came out of - and what is carried forward is only what this
    repository cannot see: the prose, and which Angular service talks to which hub - which is itself
    asserted to be filled in, because the one field a regeneration leaves empty is the one that decides
    whether the entry is checked against anything. Then `ContractTests`
    (13 cases) and `OpenApiSurfaceTests` (3), which generate the files and assert them
    against the server - the mapped hub endpoints, reflection over the hub classes, the host's own
    serializer metadata for the return payloads, and the five real event handlers driven into a recording
    hub context for the broadcast arities, because an arity only exists at a call site and reading the
    `VmHubMethods` constants would have asserted the wrong half. `crucible-tests/playerVm/tests/contract/`
    is the other side, 24 cases reading the same two files against both Angular services and the committed
    client. Six characterizations came out of it, listed in the section above; the sharpest is that
    `console.ui` has a `Complete` handler for a message no part of this application sends, which had been
    unreachable code masquerading as a feature for as long as both files have existed. The coverage effect
    was incidental and is described above. What this does not reach is stated as its own entry under "What
    is not covered yet": the group names are still asserted twice rather than compared, and the client half
    of the file is gated by a different repository's pipeline than this one's.
