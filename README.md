# Vm.Api Readme

The Vm.Api is the backend restful API for the VM application that integrates with Player to display and manage virtual machines.

## Testing

`dotnet test` runs the suite. It needs Docker, because PostgreSQL is the only database the tests use
and it is started in a container; it needs no network, vCenter or Proxmox cluster. See
[docs/Testing.md](./docs/Testing.md) for how the harness works, the conventions to follow when adding
a test, and what the suite does not cover yet.

## Machine state

vSphere machine state - power, IP addresses and snapshots - is streamed from each vCenter through the
property collector's `WaitForUpdatesEx`, over the same session the API already holds, so a newly created
machine is usually picked up within seconds, though there is no guaranteed bound. Proxmox machine state is
still polled every `StateRefreshIntervalSeconds`. A single API replica is assumed: each replica runs its
own watchers and writers. A machine removed from vCenter keeps the last state its row recorded.

## Reporting bugs and requesting features

Think you found a bug? Please report all Crucible bugs - including bugs for the individual Crucible apps - in the [cmu-sei/crucible issue tracker](https://github.com/cmu-sei/crucible/issues).

Include as much detail as possible including steps to reproduce, specific app involved, and any error messages you may have received.

Have a good idea for a new feature? Submit all new feature requests through the [cmu-sei/crucible issue tracker](https://github.com/cmu-sei/crucible/issues).

Include the reasons why you're requesting the new feature and how it might benefit other Crucible users.

## License

Copyright 2022 Carnegie Mellon University. See the [LICENSE.md](./LICENSE.md) files for details.
