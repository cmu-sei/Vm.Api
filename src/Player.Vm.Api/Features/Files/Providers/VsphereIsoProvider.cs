// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Features.Files.Providers
{
    // vSphere ISO storage. Two write modes, unchanged from before this was a provider:
    //  - UploadToDatastore: stream to every connected vCenter's datastore over its HTTP file API.
    //    Used by VMware Cloud on AWS SDDCs, which have no NFS datastore.
    //  - otherwise (the default): write into {BasePath}/{viewId}/{scopeId} on a share the hosts mount.
    //
    // Scoping is the datastore folder hierarchy, so any filename that is legal on the filesystem is
    // legal here and ValidateFilename has nothing to check.
    public class VsphereIsoProvider : IIsoProvider
    {
        private readonly IVsphereService _vsphereService;
        private readonly IsoUploadOptions _isoUploadOptions;
        private readonly VsphereOptions _vsphereOptions;

        public VsphereIsoProvider(
            IVsphereService vsphereService,
            IsoUploadOptions isoUploadOptions,
            VsphereOptions vsphereOptions)
        {
            _vsphereService = vsphereService;
            _isoUploadOptions = isoUploadOptions;
            _vsphereOptions = vsphereOptions;
        }

        public VmType ProviderType => VmType.Vsphere;

        // Intentionally blank: an upload fans out across every connected vCenter, so no single address
        // describes where a listing came from.
        public string ProviderInstanceId => string.Empty;

        // A hypervisor nobody configured is invisible rather than an error, exactly as
        // ProxmoxIsoProvider treats a missing IsoStorage. Both conditions matter: VsphereHost.Enabled
        // defaults to true in code while the shipped appsettings.json sets it false, and a host entry
        // with a blank Address can never connect.
        //
        // Deliberately config presence, NOT live connectivity: gating on GetEnabledConnectionCount()
        // would make a vCenter blip silently drop the provider, so an upload would report complete
        // success having never reached vSphere - turning a reported partial failure into a hidden one.
        public bool Enabled =>
            _vsphereOptions.Hosts?.Any(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Address)) == true;

        // The datastore path needs a seekable local file to stream to each host; the NFS path can take
        // the request body directly, which is what keeps single-scope NFS uploads free of any
        // temp-space requirement.
        public bool RequiresStagedFile => _isoUploadOptions.UploadToDatastore;

        public int TargetCount => _isoUploadOptions.UploadToDatastore
            ? _vsphereService.GetEnabledConnectionCount()
            : 1;

        // Identity. Folding names here would silently rename files for vSphere-only installs, which have
        // no reason to accept a narrower character set than the filesystem does.
        public string NormalizeFilename(string filename) => filename;

        public void ValidateFilename(Guid viewId, string scopeId, string filename)
        {
            // Nothing to enforce - folder scoping imposes no naming constraints of its own.
        }

        public async Task<IsoOperationOutcome> UploadAsync(IsoUploadRequest request, CancellationToken ct)
        {
            if (_isoUploadOptions.UploadToDatastore)
            {
                return await UploadToDatastore(request, ct);
            }

            await UploadToNfs(request, ct);

            // Zero targets, not one: an NFS write goes to a share rather than to individual hosts, so
            // there is no per-host tally to report. This is also what the response has always carried
            // for this mode, and the counts are part of the wire contract.
            return new IsoOperationOutcome();
        }

        // Stream the staged ISO to the datastore for each target scope in parallel. Each scope's
        // UploadIso internally fans out across all enabled+connected hosts and returns per-host counts.
        private async Task<IsoOperationOutcome> UploadToDatastore(IsoUploadRequest request, CancellationToken ct)
        {
            // RequiresStagedFile is true in this mode, so IsoService always hands us a file.
            if (request.StagedFilePath == null)
                throw new InvalidOperationException("The vSphere datastore upload path requires a staged file.");

            var outcomes = await Task.WhenAll(request.ScopeIds.Select(scopeId =>
                _vsphereService.UploadIso(request.ViewId.ToString(), scopeId, request.FileName, request.StagedFilePath)));

            return new IsoOperationOutcome
            {
                FailedHostCount = outcomes.Sum(o => o.FailedHostCount),
                TotalHostCount = outcomes.Sum(o => o.TotalHostCount)
            };
        }

        // NFS path: write into the target folder(s) under BasePath. The source can only be read once,
        // so the first scope consumes it and any remaining scopes are file-copied from the result.
        private async Task UploadToNfs(IsoUploadRequest request, CancellationToken ct)
        {
            string DestFileFor(string scopeId)
            {
                var destPath = Path.Combine(_isoUploadOptions.BasePath, request.ViewId.ToString(), scopeId);
                Directory.CreateDirectory(destPath);
                return Path.Combine(destPath, request.FileName);
            }

            var firstFile = DestFileFor(request.ScopeIds[0]);

            using (var sourceStream = OpenSource(request))
            {
                using var destStream = File.Create(firstFile);
                await sourceStream.CopyToAsync(destStream, ct);
            }

            await Task.WhenAll(request.ScopeIds.Skip(1).Select(async scopeId =>
            {
                using var source = File.OpenRead(firstFile);
                using var dest = File.Create(DestFileFor(scopeId));
                await source.CopyToAsync(dest, ct);
            }));
        }

        // A staged file is already a finished ISO, so both modes read bytes the same way; only where
        // they come from differs.
        private static Stream OpenSource(IsoUploadRequest request)
        {
            return request.StagedFilePath != null
                ? File.OpenRead(request.StagedFilePath)
                : request.OpenSource();
        }

        public async Task<IsoOperationOutcome> DeleteAsync(Guid viewId, string scopeId, string filename, CancellationToken ct)
        {
            if (_isoUploadOptions.UploadToDatastore)
            {
                return await _vsphereService.DeleteIso(viewId.ToString(), scopeId, filename);
            }

            // NFS path: best-effort delete; a missing file is treated as success (idempotent).
            var destFile = Path.Combine(
                _isoUploadOptions.BasePath,
                viewId.ToString(),
                scopeId,
                filename
            );

            if (File.Exists(destFile))
            {
                File.Delete(destFile);
            }

            return new IsoOperationOutcome();
        }

        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListAsync(Guid? viewId, CancellationToken ct)
        {
            // An empty listing from a provider means "this provider holds no ISOs", which the merge
            // reads as the file being MISSING here. A vCenter that is merely unreachable must not say
            // that, or every other provider's row would be badged incomplete during an outage -
            // VsphereService.ListIsos logs and returns empty in that case, so the check is here.
            // BuildViewIsoResultsAsync drops a provider that throws out of the merge entirely.
            if (_vsphereService.GetEnabledConnectionCount() == 0)
                throw new InvalidOperationException("No connected vSphere hosts available to list ISOs.");

            return Decorate(await _vsphereService.ListIsos(viewId));
        }

        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListForVmAsync(Guid vmId, Guid? viewId, CancellationToken ct)
        {
            return Decorate(await _vsphereService.ListIsosForVm(vmId, viewId));
        }

        // Stamp provenance and the mount token onto a listing. VsphereService reports the folder path
        // and filename separately because that is what the datastore browser returns; the token
        // MountIso wants is the two concatenated, and computing it here means no client has to know
        // that vSphere's paths already carry a trailing slash.
        private Dictionary<Guid, IReadOnlyList<IsoFile>> Decorate(
            IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>> isosByScope)
        {
            foreach (var iso in isosByScope.Values.SelectMany(x => x))
            {
                iso.ProviderType = ProviderType;
                iso.ProviderInstanceId = ProviderInstanceId;
                iso.MountValue = iso.Path + iso.Filename;
            }

            return isosByScope.ToDictionary(x => x.Key, x => x.Value);
        }
    }
}
