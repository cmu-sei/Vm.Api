// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox;
using Player.Vm.Api.Domain.Proxmox.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Files.Providers
{
    // Proxmox ISO storage. Two write modes, chosen with ProxmoxOptions.UploadViaApi:
    //  - false (the default): write the file into IsoRoot, a local mount of the storage's template/iso
    //    directory. PVE re-reads that directory whenever its content index is queried, so nothing has
    //    to be told the file arrived.
    //  - true: push the bytes through PVE's own storage upload API. Needed where vm.api cannot mount the
    //    storage. Separate from Vsphere:UploadViaApi so a mixed deployment can pair, say,
    //    vSphere-over-NFS with Proxmox-over-API.
    //
    // Listing goes through PVE either way, since PVE is the only thing that knows the volume ids a mount
    // will need.
    //
    // Scoping is folded into the filename (see ProxmoxIsoNaming) because a Proxmox storage keeps every
    // ISO in one flat directory and '/' is not a legal filename character - so unlike vSphere there is
    // no folder hierarchy to carry the View and team.
    public class ProxmoxIsoProvider : IIsoProvider
    {
        private readonly ProxmoxOptions _proxmoxOptions;
        private readonly IProxmoxIsoStorageService _isoStorageService;
        private readonly ILogger<ProxmoxIsoProvider> _logger;

        // No IsoUploadOptions: staging is IsoService's job, and the upload timeout belongs to the
        // "proxmoxIsoUpload" HttpClient that ProxmoxIsoStorageService uses. Nothing under IsoUpload is
        // this provider's concern.
        public ProxmoxIsoProvider(
            ProxmoxOptions proxmoxOptions,
            IProxmoxIsoStorageService isoStorageService,
            ILogger<ProxmoxIsoProvider> logger)
        {
            _proxmoxOptions = proxmoxOptions;
            _isoStorageService = isoStorageService;
            _logger = logger;

            // Guarded on Enabled so a deployment that never opted in is never blocked from starting by
            // ISO settings it has no reason to have filled in.
            if (Enabled)
            {
                ValidateConfiguration();
            }
        }

        public VmType ProviderType => VmType.Proxmox;

        // IsoStorage is the ISO opt-in and the opt-out: clearing it switches ISO support off while
        // leaving the rest of the Proxmox integration alone. It folds into Enabled rather than throwing
        // because an unconfigured Proxmox section has to leave a vSphere-only install completely
        // untouched, not fail its uploads.
        public bool Enabled =>
            _proxmoxOptions.Enabled && !string.IsNullOrWhiteSpace(_proxmoxOptions.IsoStorage);

        // Unset - which includes blank, see ProxmoxOptions.UploadViaApi - is the IsoRoot mode.
        private bool UploadViaApi => _proxmoxOptions.UploadViaApi == true;

        // The upload API sends a multipart body it has to be able to measure, so it needs a real file;
        // an IsoRoot write can take the request body as it streams.
        public bool RequiresStagedFile => UploadViaApi;

        // PVE's upload API rewrites anything outside [-a-zA-Z0-9_.] to '_'. Applying that ourselves, in
        // both write modes, keeps the name we store equal to the name delete and mount rebuild from
        // (viewId, scopeId, filename) - and keeps the two write modes agreeing about naming, so flipping
        // UploadViaApi does not orphan the files already there.
        public string NormalizeFilename(string filename) => ProxmoxIsoNaming.Normalize(filename);

        public void ValidateFilename(Guid viewId, string scopeId, string filename)
        {
            var separator = _proxmoxOptions.IsoScopeSeparator;

            // Runs after NormalizeFilename, which collapses runs of '_', so with the default '__'
            // separator this cannot fire just because a space or a parenthesis was folded to '_'.
            if (filename.Contains(separator, StringComparison.Ordinal))
            {
                throw new BadRequestException(
                    $"The filename '{filename}' cannot contain '{separator}', which Proxmox ISO storage reserves to carry the View and team.");
            }

            var encodedLength = ProxmoxIsoNaming.Encode(viewId, scopeId, filename, separator).Length;

            if (encodedLength > ProxmoxIsoNaming.MaxEncodedLength)
            {
                var overhead = encodedLength - filename.Length;

                throw new BadRequestException(
                    $"The filename '{filename}' is too long to store on Proxmox. Proxmox ISO storage is flat, so the View and team are folded into the filename at a cost of {overhead} characters against a {ProxmoxIsoNaming.MaxEncodedLength} character limit - leaving room for {ProxmoxIsoNaming.MaxEncodedLength - overhead}.");
            }
        }

        public async Task<IsoOperationOutcome> UploadAsync(IsoUploadRequest request, CancellationToken ct)
        {
            foreach (var scopeId in request.ScopeIds)
            {
                var encodedName = Encode(request.ViewId, scopeId, request.FileName);

                if (UploadViaApi)
                {
                    // RequiresStagedFile is true in this mode, so IsoService always hands us a file.
                    if (request.StagedFilePath == null)
                        throw new InvalidOperationException("The Proxmox storage upload path requires a staged file.");

                    await _isoStorageService.UploadIso(encodedName, request.StagedFilePath, ct);
                }
                else
                {
                    await WriteToIsoRoot(request, encodedName, ct);
                }
            }

            // Zero targets, matching the vSphere NFS mode: writes land on a storage rather than on
            // individual hosts, so there is no per-host tally to report and the response counts stay
            // comparable across providers.
            return new IsoOperationOutcome();
        }

        // Write into IsoRoot - one copy per scope, exactly as the vSphere share path writes one copy per
        // scope folder. Only the single-scope, single-provider case arrives with OpenSource instead of a
        // staged file (IsoService's straight-through condition), so the forward-only source is never
        // asked to produce a second copy.
        private async Task WriteToIsoRoot(IsoUploadRequest request, string encodedName, CancellationToken ct)
        {
            Directory.CreateDirectory(_proxmoxOptions.IsoRoot);

            var destFile = Path.Combine(_proxmoxOptions.IsoRoot, encodedName);

            if (request.StagedFilePath != null)
            {
                using var source = File.OpenRead(request.StagedFilePath);
                using var dest = File.Create(destFile);
                await source.CopyToAsync(dest, ct);
                return;
            }

            using var stream = request.OpenSource();
            using var target = File.Create(destFile);
            await stream.CopyToAsync(target, ct);
        }

        public async Task<IsoOperationOutcome> DeleteAsync(Guid viewId, string scopeId, string filename, CancellationToken ct)
        {
            var encodedName = Encode(viewId, scopeId, filename);

            if (UploadViaApi)
            {
                await _isoStorageService.DeleteIso(encodedName, ct);
            }
            else
            {
                // Best-effort, like the vSphere share path: a file that is already gone is success.
                var destFile = Path.Combine(_proxmoxOptions.IsoRoot, encodedName);

                if (File.Exists(destFile))
                {
                    File.Delete(destFile);
                }
            }

            return new IsoOperationOutcome();
        }

        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoListingEntry>>> ListAsync(Guid? viewId, CancellationToken ct)
        {
            return GroupByScope(await _isoStorageService.ListIsos(ct), viewId);
        }

        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoListingEntry>>> ListForVmAsync(Guid vmId, Guid? viewId, CancellationToken ct)
        {
            return GroupByScope(await _isoStorageService.ListIsosForVm(vmId, ct), viewId);
        }

        // A PVE volume id names any volume on any storage the cluster has - including another VM's disk
        // image, which would be readable as a CD once mounted - so nothing here trusts the submitted
        // string. It has to be on the one configured ISO storage and carry a decodable Player scope, and
        // what comes back out is rebuilt from the decoded parts rather than echoed.
        public Task<IsoMountTarget> ResolveMountTargetAsync(Guid vmId, string mountValue, CancellationToken ct)
        {
            // No I/O and no need for the VM: a Proxmox ISO's scope is entirely in its name, and the
            // storage is cluster-wide, so there is nothing host-specific to resolve.
            return Task.FromResult(ResolveMountTarget(mountValue));
        }

        internal IsoMountTarget ResolveMountTarget(string mountValue)
        {
            if (string.IsNullOrWhiteSpace(mountValue))
                return null;

            // A volid is "{storage}:{path}". Anchoring on the configured storage rejects every volume
            // outside it, so a disk image on 'local' cannot be named at all.
            if (!mountValue.StartsWith(_proxmoxOptions.IsoStorage + ":", StringComparison.Ordinal))
                return null;

            var separator = _proxmoxOptions.IsoScopeSeparator;

            // VolumeFileName takes the last '/' segment, so a traversal attempt collapses to its final
            // component - which then has to decode as a Player-scoped ISO name like any other.
            if (!ProxmoxIsoNaming.TryDecode(
                    ProxmoxIsoNaming.VolumeFileName(mountValue), separator,
                    out var viewId, out var scopeId, out var displayName))
                return null;

            // Rebuilt, not compared: PVE reports both "storage:iso/x" and "storage:/iso/x" and a listing
            // hands the volid back verbatim, so the rebuild is what makes the two spellings one token.
            var canonical = ProxmoxIsoNaming.BuildVolumeId(
                _proxmoxOptions.IsoStorage,
                ProxmoxIsoNaming.Encode(viewId, scopeId.ToString(), displayName, separator));

            return new IsoMountTarget(viewId, scopeId, displayName, canonical);
        }

        // Decode the scope back out of each filename and bucket by it, dropping anything that is not one
        // of ours. A Proxmox ISO storage is routinely shared with hand-placed installer ISOs and PVE's
        // own templates, none of which belong to any View, so surfacing them under an arbitrary one
        // would be worse than hiding them.
        private Dictionary<Guid, IReadOnlyList<IsoListingEntry>> GroupByScope(
            IReadOnlyList<ProxmoxIsoVolume> volumes,
            Guid? viewId)
        {
            var separator = _proxmoxOptions.IsoScopeSeparator;
            var grouped = new Dictionary<Guid, List<IsoListingEntry>>();
            var skipped = 0;

            foreach (var volume in volumes)
            {
                if (!ProxmoxIsoNaming.TryDecode(volume.FileName, separator, out var decodedViewId, out var scopeId, out var displayName))
                {
                    skipped++;
                    continue;
                }

                if (viewId.HasValue && decodedViewId != viewId.Value)
                    continue;

                if (!grouped.TryGetValue(scopeId, out var isos))
                {
                    isos = [];
                    grouped[scopeId] = isos;
                }

                // The volume id is carried verbatim from PVE, so it is immune to any normalization PVE
                // applied on the way in.
                isos.Add(new IsoListingEntry(displayName, volume.VolumeId));
            }

            if (skipped > 0)
            {
                _logger.LogDebug(
                    "Skipped {Count} file(s) on Proxmox storage {Storage} that are not Player-scoped ISOs",
                    skipped, _proxmoxOptions.IsoStorage);
            }

            return grouped.ToDictionary(x => x.Key, x => (IReadOnlyList<IsoListingEntry>)x.Value);
        }

        // Normalize here rather than relying on the caller: a name that came through an upload is
        // already a fixed point, but a delete of a file that predates this provider (a vSphere-only
        // install that later configured Proxmox) arrives verbatim, and Proxmox stores it folded.
        private string Encode(Guid viewId, string scopeId, string filename) =>
            ProxmoxIsoNaming.Encode(
                viewId, scopeId, ProxmoxIsoNaming.Normalize(filename), _proxmoxOptions.IsoScopeSeparator);

        // Fail at startup rather than on the first upload: every one of these is a deployment mistake
        // that would otherwise surface as a confusing runtime error, or worse, as files written where
        // nothing will ever read them.
        private void ValidateConfiguration()
        {
            var separator = _proxmoxOptions.IsoScopeSeparator;

            if (string.IsNullOrEmpty(separator))
            {
                throw new InvalidOperationException(
                    "Proxmox:IsoScopeSeparator cannot be empty - it is what carries the View and team in a Proxmox ISO filename.");
            }

            // PVE's upload API rewrites anything outside [-a-zA-Z0-9_.], which would silently mangle the
            // separator and make every uploaded ISO undecodable. Only enforced in the mode that goes
            // through that API; an IsoRoot write stores whatever it is given.
            if (UploadViaApi && !ProxmoxIsoNaming.SurvivesUpload(separator))
            {
                throw new InvalidOperationException(
                    $"Proxmox:IsoScopeSeparator '{separator}' cannot be used with Proxmox:UploadViaApi, because Proxmox's upload API rewrites any character outside [-a-zA-Z0-9_.]. Use '__'.");
            }

            if (!UploadViaApi && string.IsNullOrWhiteSpace(_proxmoxOptions.IsoRoot))
            {
                throw new InvalidOperationException(
                    "Proxmox:IsoRoot is required when Proxmox:UploadViaApi is false. Set it to a local mount of the ISO storage's template/iso directory, or enable Proxmox:UploadViaApi to push ISOs through Proxmox's API instead.");
            }
        }
    }
}
