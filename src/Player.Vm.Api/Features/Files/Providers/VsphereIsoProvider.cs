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

namespace Player.Vm.Api.Features.Files.Providers
{
    // vSphere ISO storage. Two write modes, chosen with VsphereOptions.IsoUploadViaApi:
    //  - true: stream to every connected vCenter's datastore over its HTTP file API, landing at
    //    "[{DsName}] {BaseFolder}/{viewId}/{scopeId}/{filename}" on each host's own datastore. Used by
    //    VMware Cloud on AWS SDDCs, which have no NFS datastore.
    //  - false (the default): write into {IsoRoot}/{viewId}/{scopeId} on a share the hosts mount, which
    //    has to be the share that surfaces as "[{DsName}] {BaseFolder}" - listing browses the datastore
    //    in both modes, so the two layouts are the same layout reached two ways.
    //
    // Scoping is the datastore folder hierarchy, so any filename that is legal on the filesystem is
    // legal here and ValidateFilename has nothing to check.
    public class VsphereIsoProvider : IIsoProvider
    {
        private readonly IVsphereService _vsphereService;
        private readonly VsphereOptions _vsphereOptions;

        // No IsoUploadOptions: the shared pipeline's staging and limits are IsoService's concern, and
        // where these bytes land is entirely under Vsphere:.
        public VsphereIsoProvider(
            IVsphereService vsphereService,
            VsphereOptions vsphereOptions)
        {
            _vsphereService = vsphereService;
            _vsphereOptions = vsphereOptions;
        }

        public VmType ProviderType => VmType.Vsphere;

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

        // Unset - which includes blank, see VsphereOptions.IsoUploadViaApi - is the IsoRoot mode.
        private bool IsoUploadViaApi => _vsphereOptions.IsoUploadViaApi == true;

        // The datastore path needs a seekable local file to stream to each host; the IsoRoot path can
        // take the request body directly, which is what keeps single-scope share uploads free of any
        // temp-space requirement.
        public bool RequiresStagedFile => IsoUploadViaApi;

        // Identity. Folding names here would silently rename files for vSphere-only installs, which have
        // no reason to accept a narrower character set than the filesystem does.
        public string NormalizeFilename(string filename) => filename;

        public void ValidateFilename(Guid viewId, string scopeId, string filename)
        {
            // Nothing to enforce - folder scoping imposes no naming constraints of its own.
        }

        public async Task<IsoOperationOutcome> UploadAsync(IsoUploadRequest request, CancellationToken ct)
        {
            if (IsoUploadViaApi)
            {
                return await UploadToDatastore(request, ct);
            }

            await UploadToIsoRoot(request, ct);

            // Zero targets, not one: an IsoRoot write goes to a share rather than to individual hosts, so
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

        // Share path: write into the target folder(s) under IsoRoot. The source can only be read once,
        // so the first scope consumes it and any remaining scopes are file-copied from the result.
        private async Task UploadToIsoRoot(IsoUploadRequest request, CancellationToken ct)
        {
            string DestFileFor(string scopeId)
            {
                var destPath = Path.Combine(IsoRoot(), request.ViewId.ToString(), scopeId);
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
            if (IsoUploadViaApi)
            {
                return await _vsphereService.DeleteIso(viewId.ToString(), scopeId, filename);
            }

            // Share path: best-effort delete; a missing file is treated as success (idempotent).
            var destFile = Path.Combine(
                IsoRoot(),
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

        // The share directory, or a clear failure naming the key that is missing. Checked at the point
        // of use rather than at startup: IsoOptionsCheck logs the misconfiguration but
        // deliberately does not block the API from booting, so this is what has to be legible when an
        // un-migrated deployment attempts its first upload. Without it a blank IsoRoot would silently
        // Path.Combine into a relative directory next to the process.
        private string IsoRoot()
        {
            if (string.IsNullOrWhiteSpace(_vsphereOptions.IsoRoot))
            {
                throw new InvalidOperationException(
                    "Vsphere:IsoRoot is required when Vsphere:IsoUploadViaApi is false. Set it to a directory on a share the vSphere hosts also mount, or enable Vsphere:IsoUploadViaApi to push ISOs to the datastore through vCenter's HTTP file API instead.");
            }

            return _vsphereOptions.IsoRoot;
        }

        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoListingEntry>>> ListAsync(Guid? viewId, CancellationToken ct)
        {
            // An empty listing from a provider means "this provider holds no ISOs", which the merge
            // reads as the file being MISSING here. A vCenter that is merely unreachable must not say
            // that, or every other provider's row would be badged incomplete during an outage -
            // VsphereService.ListIsos logs and returns empty in that case, so the check is here.
            // BuildViewIsoResultsAsync drops a provider that throws out of the merge entirely.
            if (_vsphereService.GetEnabledConnectionCount() == 0)
                throw new InvalidOperationException("No connected vSphere hosts available to list ISOs.");

            return await _vsphereService.ListIsos(viewId);
        }

        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoListingEntry>>> ListForVmAsync(Guid vmId, Guid? viewId, CancellationToken ct)
        {
            return await _vsphereService.ListIsosForVm(vmId, viewId);
        }

        // A datastore path names any file the datastore will serve - another View's ISO, another team's,
        // or anything else readable - so a submitted path is never trusted. It has to sit exactly where
        // this provider writes ISOs, on the datastore of the host THIS VM is reached through, and the
        // path that gets mounted is rebuilt from the decoded parts rather than echoed back.
        public async Task<IsoMountTarget> ResolveMountTargetAsync(Guid vmId, string mountValue, CancellationToken ct)
        {
            // The layout is per-host, so a path listed from some other vCenter must not authorize a
            // mount here - which is the same host-affinity rule ListIsosForVm follows.
            return ResolveMountTarget(await _vsphereService.GetHostForVm(vmId), mountValue);
        }

        // Split out and internal so the tests can drive every rejection case with a plain host config
        // and no vSphere at all. Pure, and the whole security boundary for a vSphere mount.
        internal static IsoMountTarget ResolveMountTarget(VsphereHost host, string mountValue)
        {
            if (host == null || string.IsNullOrWhiteSpace(host.DsName) || string.IsNullOrWhiteSpace(mountValue))
                return null;

            // Anchoring on the datastore rejects every other datastore the host can see. Case-insensitive
            // because the datastore browser echoes vSphere's own spelling, which need not match config's.
            var prefix = $"[{host.DsName}] ";

            if (!mountValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var baseFolder = (host.BaseFolder ?? string.Empty).Trim('/');
            var baseSegments = baseFolder.Length == 0 ? [] : baseFolder.Split('/');
            var segments = mountValue[prefix.Length..].Split('/');

            // Exactly the ISO layout: {baseFolder}/{viewId}/{scopeId}/{filename}. A fixed segment count
            // is what stops both a shallower path (the View or base folder itself) and a deeper one.
            if (segments.Length != baseSegments.Length + 3)
                return null;

            // No empty, '.', '..' or otherwise nested segment survives, so nothing can climb out of the
            // ISO tree or smuggle a separator through the filename.
            if (segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.Contains('\\')))
                return null;

            if (baseSegments.Where((s, i) => !string.Equals(s, segments[i], StringComparison.OrdinalIgnoreCase)).Any())
                return null;

            if (!Guid.TryParse(segments[^3], out var viewId) || !Guid.TryParse(segments[^2], out var scopeId))
                return null;

            var filename = segments[^1];

            if (!IsoFileNaming.IsIsoFile(filename))
                return null;

            // Rebuilt through the same helper the search and the upload use, so a change to the layout
            // cannot leave this parse authorizing paths nothing writes to any more.
            var folder = baseSegments.Length == 0
                ? $"{viewId}/{scopeId}"
                : VsphereService.BuildIsoFolderRelative(baseFolder, viewId.ToString(), scopeId.ToString());

            return new IsoMountTarget(viewId, scopeId, filename, $"[{host.DsName}] {folder}/{filename}");
        }
    }
}
