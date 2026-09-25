# Vm.Api Readme

The Vm.Api is the backend restful API for the VM application that integrates with Player to display and manage virtual machines.

## Testing

`dotnet test` runs the suite. It needs Docker, because PostgreSQL is the only database the tests use
and it is started in a container; it needs no network, vCenter or Proxmox cluster. See
[docs/Testing.md](./docs/Testing.md) for how the harness works, the conventions to follow when adding
a test, and what the suite does not cover yet.

## Initializing newly registered VMs

New VMs are discovered in the background after their database transaction commits. Each provider
collects arrivals until there have been two quiet seconds, or five seconds have elapsed since the
first arrival. Configure these values through the `VmInitialization` section:

```json
"VmInitialization": {
  "DebounceSeconds": 2,
  "MaxWaitSeconds": 5
}
```

Both values must be positive whole seconds (at most 86400), and `MaxWaitSeconds` must be at least
`DebounceSeconds`. Environment equivalents are `VmInitialization__DebounceSeconds` and
`VmInitialization__MaxWaitSeconds`. Reloaded settings apply to subsequent batches.

The maximum wait bounds batching delay, not provider response time. vSphere looks up only the new
VMs, with at most eight concurrent UUID searches, then fetches their properties in batches per
vCenter. Proxmox reads cluster resources once per batch and updates only the new records.
Unresolved VMs become eligible for retries after 2, 5, and 15 seconds; retries join the debounce
queue. Periodic reconciliation remains enabled and recovers work interrupted by an API restart.
VM creation does not wait for discovery, so the initial response can still report `Unknown`.

## Reporting bugs and requesting features

Think you found a bug? Please report all Crucible bugs - including bugs for the individual Crucible apps - in the [cmu-sei/crucible issue tracker](https://github.com/cmu-sei/crucible/issues).

Include as much detail as possible including steps to reproduce, specific app involved, and any error messages you may have received.

Have a good idea for a new feature? Submit all new feature requests through the [cmu-sei/crucible issue tracker](https://github.com/cmu-sei/crucible/issues).

Include the reasons why you're requesting the new feature and how it might benefit other Crucible users.

## License

Copyright 2022 Carnegie Mellon University. See the [LICENSE.md](./LICENSE.md) files for details.
