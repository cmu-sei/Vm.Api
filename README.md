# Vm.Api Readme

The Vm.Api is the backend restful API for the VM application that integrates with Player to display and manage virtual machines.

## Testing

`dotnet test` runs the suite. It needs Docker, because PostgreSQL is the only database the tests use
and it is started in a container; it needs no network, vCenter or Proxmox cluster. See
[docs/Testing.md](./docs/Testing.md) for how the harness works, the conventions to follow when adding
a test, and what the suite does not cover yet.

## Reporting bugs and requesting features

Think you found a bug? Please report all Crucible bugs - including bugs for the individual Crucible apps - in the [cmu-sei/crucible issue tracker](https://github.com/cmu-sei/crucible/issues).

Include as much detail as possible including steps to reproduce, specific app involved, and any error messages you may have received.

Have a good idea for a new feature? Submit all new feature requests through the [cmu-sei/crucible issue tracker](https://github.com/cmu-sei/crucible/issues).

Include the reasons why you're requesting the new feature and how it might benefit other Crucible users.

## ISO storage groups

In vSphere API mode (`Vsphere:IsoUploadViaApi: true`), uploads and deletes run once per
configured ISO destination. A destination can be reached through several vCenters: the API tries
connected members in configuration order and stops after one succeeds. Different destinations run
in parallel. Each member keeps its own `DsName` and `BaseFolder`; group membership declares that
these paths reach the **same physical ISO directory**. There is no automatic discovery.

| Setting | Default | Behavior |
|---|---|---|
| `Vsphere:IsoStorageShared` | `false` | Put hosts without an explicit group into one shared destination |
| `Vsphere:Hosts[n]:IsoStorageGroup` | Unset | Explicit destination group; overrides the global default |

Group names are trimmed and case-sensitive; blank means unassigned. Missing or blank
`IsoStorageShared` means false. With sharing off, every unassigned vCenter is a separate destination.
Explicit groups are separate from the implicit shared group, regardless of their names.

For **all vCenters sharing one ISO directory**, keep existing host settings and add:

~~~json
{ "Vsphere": { "IsoUploadViaApi": true, "IsoStorageShared": true } }
~~~

For **multiple storage groups**, leave `IsoStorageShared` false and assign the same
`IsoStorageGroup` to hosts that share each directory. For example, hosts 0 and 1 can have
`"east"`, while hosts 2 and 3 have `"west"`. An ungrouped host gets its own destination.

For **shared storage with exceptions**, set `IsoStorageShared` true and give each separate
storage destination an explicit group. These environment variables share unassigned hosts and
put host 10 in its own destination (additional hosts with `separate-isos` would join that group):

~~~text
Vsphere__IsoUploadViaApi=true
Vsphere__IsoStorageShared=true
Vsphere__Hosts__10__IsoStorageGroup=separate-isos
~~~

Disabled hosts and hosts without addresses are excluded. A required destination whose members are
all disconnected still reports failure. A failed attempt falls back to the next connected member;
uploads restart from the beginning of the staged file. Known disconnected members are skipped
immediately, but an unresponsive attempt can delay fallback until the existing operation timeout
expires. Cancelling the request stops further attempts. A missing file counts as a successful delete.

Successful fallback is a successful destination, not a partial failure. The legacy response fields
`TotalHostCount` and `FailedHostCount` count destinations **per scope**, not vCenters or retry
attempts. Partial success is reported when some destinations succeed; if none succeed on any
provider, the operation fails. Attempt details and group names appear only in server logs.
Previously, disconnected independent vCenters could be omitted from writes; they now count as
failed destinations. Existing healthy installations keep one write per host unless grouping is enabled.

Grouping only affects API writes. Listing and mounting continue through each VM's vCenter, and
local `IsoRoot` writes are unchanged. No database migration is needed. Restart the API after changing
these deployment settings.

## License

Copyright 2022 Carnegie Mellon University. See the [LICENSE.md](./LICENSE.md) files for details.
