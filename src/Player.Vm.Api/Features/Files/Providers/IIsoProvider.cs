// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Files.Models;

namespace Player.Vm.Api.Features.Files.Providers
{
    // One hypervisor's view of View/team-scoped ISO storage. A single vm.api can have several
    // providers enabled at once (a View routinely holds both vSphere and Proxmox VMs), so IsoService
    // fans upload/delete/list out across every enabled provider rather than picking one.
    //
    // Per-provider *host* fan-out stays inside the implementation: VsphereIsoProvider writes to all
    // connected vCenters itself, which is why the outcome counts are per-host and TargetCount exists.
    public interface IIsoProvider
    {
        VmType ProviderType { get; }

        // Identifies which deployment of this provider a listing came from, for logs and for the UI's
        // "missing on X" detail. Proxmox reports its cluster host; vSphere reports "" because its
        // multi-vCenter fan-out is internal and no single address describes the result.
        string ProviderInstanceId { get; }

        // False when this provider is not configured for ISOs at all, in which case IsoService skips
        // it entirely. An unconfigured provider must be invisible, not an error - that is what keeps
        // an existing vSphere-only install behaving exactly as it did.
        bool Enabled { get; }

        // Number of write targets a single scope's upload touches, used only for the response counts.
        int TargetCount { get; }

        // True when this provider cannot consume a forward-only stream and needs a file on disk.
        // IsoService uses this - not a mode flag it would have to know about - to decide whether an
        // upload can stream straight through or has to be staged first.
        bool RequiresStagedFile { get; }

        // Fold a display filename into the character set this provider can store faithfully. IsoService
        // applies every enabled provider's normalizer, in turn, before validating or writing anything,
        // so one uploaded file ends up with one name on every hypervisor - which is what lets the Files
        // tab merge rows by name across providers.
        //
        // vSphere returns the name unchanged. Proxmox has to fold it, because PVE's storage upload API
        // rewrites anything outside [-a-zA-Z0-9_.] to '_' on its own; doing it ourselves up front keeps
        // the stored name equal to the one delete and mount reconstruct from (viewId, scopeId, filename).
        //
        // Must be idempotent: with several providers enabled it is applied more than once.
        string NormalizeFilename(string filename);

        // Reject a name this provider cannot store, BEFORE anything is written anywhere. IsoService
        // calls this for every enabled provider and every scope up front, so a name that is illegal on
        // one hypervisor fails the whole upload with a 400 instead of half-succeeding.
        void ValidateFilename(Guid viewId, string scopeId, string filename);

        Task<IsoOperationOutcome> UploadAsync(IsoUploadRequest request, CancellationToken ct);

        Task<IsoOperationOutcome> DeleteAsync(Guid viewId, string scopeId, string filename, CancellationToken ct);

        // Every ISO this provider holds, keyed on scope id (view id for view-wide, else team id).
        // Scoped to one View when viewId is given.
        Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListAsync(Guid? viewId, CancellationToken ct);

        // Same, but from the storage the given VM can actually reach. The results feed the mount
        // picker and are handed back to a mount command, so they must be actionable for that VM -
        // see VsphereService.ListIsosForVm and the node handling in ProxmoxIsoProvider.
        Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListForVmAsync(Guid vmId, Guid? viewId, CancellationToken ct);

        // The reverse of this provider's naming scheme: read the scope back out of a mount value a
        // client submitted, and rebuild the canonical token for those parts. Only the provider can do
        // this - the scope lives in the filename on Proxmox and in the folder path on vSphere.
        //
        // Null for anything this provider would not itself have issued for this VM, which is what makes
        // it an authorization primitive rather than a parser: a foreign storage or datastore, a disk
        // image, a traversal attempt, or a name that was never Player-scoped all fail here. The VM
        // matters because vSphere's layout is per-host (DsName/BaseFolder).
        //
        // Callers mount IsoMountTarget.MountValue, never the string they were given.
        Task<IsoMountTarget> ResolveMountTargetAsync(Guid vmId, string mountValue, CancellationToken ct);
    }

    // A mount value decoded back to the scope that owns it. ScopeId equal to ViewId means a view-scoped
    // ISO, otherwise it is a team id - the same convention the write path uses for its scope folder /
    // filename segment. MountValue is the canonical token rebuilt from the other three fields.
    public sealed record IsoMountTarget(Guid ViewId, Guid ScopeId, string FileName, string MountValue);

    // The upload payload, shared by IsoService and every provider so there is one shape end to end.
    // Exactly one of StagedFilePath / OpenSource is set:
    //  - StagedFilePath: IsoService already wrote the ISO to a local temp file. Re-readable, so it can
    //    be handed to several providers and scopes.
    //  - OpenSource: the request body, straight through. Single-use and forward-only, so IsoService
    //    only ever chooses this when exactly one provider and one scope are targeted.
    // FileName is the already-sanitized, ".iso"-normalized display name; each provider maps it to its
    // own on-disk layout (folders for vSphere, an encoded filename for Proxmox).
    public sealed record IsoUploadRequest(
        Guid ViewId,
        IReadOnlyList<string> ScopeIds,
        string FileName,
        string StagedFilePath,
        Func<Stream> OpenSource);
}
