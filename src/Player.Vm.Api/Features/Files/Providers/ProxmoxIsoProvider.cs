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
    // Proxmox ISO storage. Two write modes, chosen with ProxmoxOptions.UploadToStorage:
    //  - false (the default): write the file into IsoRoot, a local mount of the storage's template/iso
    //    directory. PVE re-reads that directory whenever its content index is queried, so nothing has
    //    to be told the file arrived. This is the shape TopoMojo runs in production.
    //  - true: push the bytes through PVE's own storage upload API. Needed where vm.api cannot mount the
    //    storage. Deliberately a separate flag from IsoUpload.UploadToDatastore so a mixed deployment
    //    can pair, say, vSphere-over-NFS with Proxmox-over-API.
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
        private readonly IProxmoxService _proxmoxService;
        private readonly ILogger<ProxmoxIsoProvider> _logger;

        // No IsoUploadOptions: staging is IsoService's job, and the upload timeout belongs to the
        // "proxmoxIsoUpload" HttpClient that ProxmoxService uses. Nothing under IsoUpload is this
        // provider's concern.
        public ProxmoxIsoProvider(
            ProxmoxOptions proxmoxOptions,
            IProxmoxService proxmoxService,
            ILogger<ProxmoxIsoProvider> logger)
        {
            _proxmoxOptions = proxmoxOptions;
            _proxmoxService = proxmoxService;
            _logger = logger;

            // Guarded on Enabled so a deployment that never opted in is never blocked from starting by
            // ISO settings it has no reason to have filled in.
            if (Enabled)
            {
                ValidateConfiguration();
            }
        }

        public VmType ProviderType => VmType.Proxmox;

        public string ProviderInstanceId => _proxmoxOptions.Host;

        // IsoEnabled null follows the cluster's own Enabled flag, so an existing Proxmox deployment gains
        // ISO support the moment it sets IsoStorage with no second flag to remember - while still being
        // able to switch ISOs off on their own. IsoStorage folds into Enabled rather than throwing
        // because an unconfigured Proxmox section has to leave a vSphere-only install completely
        // untouched, not fail its uploads.
        public bool Enabled =>
            (_proxmoxOptions.IsoEnabled ?? _proxmoxOptions.Enabled)
            && !string.IsNullOrWhiteSpace(_proxmoxOptions.IsoStorage);

        // One storage, however many nodes front it, so an upload has exactly one write target.
        public int TargetCount => 1;

        // The upload API sends a multipart body it has to be able to measure, so it needs a real file;
        // an IsoRoot write can take the request body as it streams.
        public bool RequiresStagedFile => _proxmoxOptions.UploadToStorage;

        // PVE's upload API rewrites anything outside [-a-zA-Z0-9_.] to '_'. Applying that ourselves, in
        // both write modes, keeps the name we store equal to the name delete and mount rebuild from
        // (viewId, scopeId, filename) - and keeps the two write modes agreeing about naming, so flipping
        // UploadToStorage does not orphan the files already there.
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

                if (_proxmoxOptions.UploadToStorage)
                {
                    // RequiresStagedFile is true in this mode, so IsoService always hands us a file.
                    if (request.StagedFilePath == null)
                        throw new InvalidOperationException("The Proxmox storage upload path requires a staged file.");

                    await _proxmoxService.UploadIsoToStorage(encodedName, request.StagedFilePath, ct);
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

        // Write into IsoRoot - one copy per scope, exactly as the vSphere NFS path writes one copy per
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

            if (_proxmoxOptions.UploadToStorage)
            {
                await _proxmoxService.DeleteIsoFromStorage(encodedName, ct);
            }
            else
            {
                // Best-effort, like the vSphere NFS path: a file that is already gone is success.
                var destFile = Path.Combine(_proxmoxOptions.IsoRoot, encodedName);

                if (File.Exists(destFile))
                {
                    File.Delete(destFile);
                }
            }

            return new IsoOperationOutcome();
        }

        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListAsync(Guid? viewId, CancellationToken ct)
        {
            return GroupByScope(await _proxmoxService.ListStorageIsos(ct), viewId);
        }

        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListForVmAsync(Guid vmId, Guid? viewId, CancellationToken ct)
        {
            return GroupByScope(await _proxmoxService.ListStorageIsosForVm(vmId, ct), viewId);
        }

        // Decode the scope back out of each filename and bucket by it, dropping anything that is not one
        // of ours. A Proxmox ISO storage is routinely shared with hand-placed installer ISOs, PVE's own
        // templates, and (where TopoMojo shares the store) its 2-segment names - none of which belong to
        // any View, so surfacing them under an arbitrary one would be worse than hiding them.
        private Dictionary<Guid, IReadOnlyList<IsoFile>> GroupByScope(
            IReadOnlyList<ProxmoxIsoVolume> volumes,
            Guid? viewId)
        {
            var separator = _proxmoxOptions.IsoScopeSeparator;
            var grouped = new Dictionary<Guid, List<IsoFile>>();
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

                isos.Add(new IsoFile(null, displayName)
                {
                    // Path stays null: a Proxmox ISO has no folder, and the token a mount takes is the
                    // volume id, carried verbatim from PVE so it is immune to any normalization PVE
                    // applied on the way in.
                    MountValue = volume.VolumeId,
                    ProviderType = ProviderType,
                    ProviderInstanceId = ProviderInstanceId
                });
            }

            if (skipped > 0)
            {
                _logger.LogDebug(
                    "Skipped {Count} file(s) on Proxmox storage {Storage} that are not Player-scoped ISOs",
                    skipped, _proxmoxOptions.IsoStorage);
            }

            return grouped.ToDictionary(x => x.Key, x => (IReadOnlyList<IsoFile>)x.Value);
        }

        private string Encode(Guid viewId, string scopeId, string filename) =>
            ProxmoxIsoNaming.Encode(viewId, scopeId, filename, _proxmoxOptions.IsoScopeSeparator);

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
            // through that API; an IsoRoot write stores whatever it is given, so an install already
            // using '#' there keeps working.
            if (_proxmoxOptions.UploadToStorage && !ProxmoxIsoNaming.SurvivesUpload(separator))
            {
                throw new InvalidOperationException(
                    $"Proxmox:IsoScopeSeparator '{separator}' cannot be used with UploadToStorage, because Proxmox's upload API rewrites any character outside [-a-zA-Z0-9_.]. Use '__'.");
            }

            if (!_proxmoxOptions.UploadToStorage && string.IsNullOrWhiteSpace(_proxmoxOptions.IsoRoot))
            {
                throw new InvalidOperationException(
                    "Proxmox:IsoRoot is required when Proxmox:UploadToStorage is false. Set it to a local mount of the ISO storage's template/iso directory, or enable UploadToStorage to push ISOs through Proxmox's API instead.");
            }
        }
    }
}
