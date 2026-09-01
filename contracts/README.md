# Contracts

Two files, both of them a *shared list*: something this repository and something outside it both
depend on, where neither side can see the other's copy and a disagreement is silent rather than a
build failure.

**Both are generated. Neither is authored, and neither is a test.** Each one is written by a test in
this repository out of what the application actually does, and is what a test on the other side of
the estate compares the browser clients to - which is the only thing that makes the two sides
comparable at all. Regenerate either with:

```sh
VMAPI_UPDATE_CONTRACTS=1 dotnet test --filter FullyQualifiedName~ContractTests
VMAPI_UPDATE_CONTRACTS=1 dotnet test --filter FullyQualifiedName~OpenApiSurfaceTests
```

Each command rewrites its file and then **fails on purpose**. Regenerating is not a way of passing: a
run with the variable set must never be green, or a pipeline that inherited it would rewrite the file
on every build and the test would never say anything again. Read the diff and re-run without the
variable.

## `signalr-contract.json`

The hub paths, the methods clients invoke, the messages the server broadcasts and the number of
arguments each carries, which producer sends each one, and the `modifiedProperties` names `VmUpdated`
sends.

Every one of those strings is written twice in the estate: once here and once in
`player/vm.ui` or `player/console.ui`. SignalR does nothing about a mismatch. An invocation the
server does not declare fails that one call and leaves the connection up; a broadcast name only one
side knows about arrives nowhere, and a Vm list in a browser stays silently stale until it is
reloaded.

- Generated and asserted against the server by `src/Player.Vm.Api.Tests/ContractTests.cs`, which
  reflects over the mapped hubs and drives the real broadcast producers rather than restating what
  they send. The paths come from the endpoints `MapHub` added, and the `sentBy` lists from which
  producer each observed send came out of.
- Asserted against the clients by
  `crucible-tests/playerVm/tests/contract/signalr-contract.spec.ts`, which reads the two Angular
  services out of the app repositories and matches the names and arities it finds against this file.

Regeneration writes into this file rather than replacing it, so four things survive it and are the
parts to edit by hand - all of them facts a repository that cannot see the browser clients could not
produce:

- the `description` fields,
- the per-entry `note` prose,
- the `clients` lists, naming which Angular service talks to which hub,
- `clientListenersWithNoSender`, where a handler with no sender is recorded rather than tolerated.

Of those, `clients` is the one a regeneration can leave you owing. A newly mapped hub is written out with
an empty list, and an empty list is what makes the entry inert: `crucible-tests` builds its per-client
checks by looping it, so a hub with no clients named is in this file, reads as covered, and is compared to
nothing. `ContractTests.EveryHubInTheContract_NamesAClientThatTalksToIt` fails until the list is filled in
or the hub is named in `HubsWithNoBrowserClient`. On the other side, a `source` path that does not exist in
a repository that *is* checked out fails rather than skipping, so an entry left behind when a client's
service file moved is not mistaken for a workspace that is missing the client.

The `progress` hub's `broadcasts` are the one structural exception, and the one thing here still
written by hand. `Progress` has no constant and no event handler behind it - both task pollers write
the literal - so driving it would mean standing up a whole poller harness for a fact
`TaskServiceTests` and `ProxmoxTaskServiceTests` already establish. `ContractTests.cs` names the
exception in `HubsWhoseBroadcastsAreDriven` and keeps the entry honest in
`TheProgressBroadcast_IsTheLiteralBothPollersSend`.

## `openapi-surface.json`

A derived summary of `/swagger/v1/swagger.json`: for every operation its method, path, tags,
`operationId`, parameters, request body and response types, and for every schema its property names
and types. Nothing in it is carried forward - regenerating replaces the file outright - and
`vm.ui`'s client should be regenerated in the same change.

`vm.ui` checks in the client generated from that document (`src/app/generated/vm-api`, produced by
`npm run swagger:gen`) and nothing regenerates it against a running API, so a renamed DTO property or
a renamed `operationId` compiles on both sides and fails at runtime. This file is the freshness check:
it goes red on exactly the changes that would make the checked-in client wrong, and the diff names
them.

- Asserted against the API by `src/Player.Vm.Api.Tests/OpenApiSurfaceTests.cs`, which fetches the
  document from the hosted application and compares the summary it derives to this file.
- Asserted against the committed client by
  `crucible-tests/playerVm/tests/contract/openapi-surface.spec.ts`, which compares this file to
  `vm.ui/src/app/generated/vm-api` - the schema set against the generated models, each schema's
  properties against the generated interface, each enum's values against the generated union, and every
  `operationId` against a method on the service its tag names.

It is a summary rather than the document itself for two reasons. The raw document is 170KB of mostly
XML doc comments, and a snapshot of it reddens when a `<summary>` is reworded - a test that fails for
reasons that do not matter gets regenerated without being read. And a summary diff is legible: the
line that changed is the property that changed.
