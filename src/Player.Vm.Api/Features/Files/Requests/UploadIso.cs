// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Iso9660;
using Microsoft.AspNetCore.Http;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Features.Files.Requests
{
    // Uploads a file as an ISO to a View's view-wide folder or to one/more team folders, writing it
    // to every resolved scope - either to the vSphere datastore(s) or to the NFS base path, depending
    // on configuration. Only the datastore path stages a local copy first (it needs a seekable file
    // per host); the NFS path writes straight through. The controller owns reading the form; this
    // handler owns the write orchestration.
    public class UploadIso : IFeatureHandler
    {
        private readonly IIsoService _isoService;
        private readonly IVsphereService _vsphereService;
        private readonly IsoUploadOptions _isoUploadOptions;

        public UploadIso(
            IIsoService isoService,
            IVsphereService vsphereService,
            IsoUploadOptions isoUploadOptions)
        {
            _isoService = isoService;
            _vsphereService = vsphereService;
            _isoUploadOptions = isoUploadOptions;
        }

        public async Task<IsoUploadResult> HandleAsync(Guid viewId, IFormFile file, string scope, long reportedSize, IReadOnlyList<Guid> teamIds, CancellationToken ct)
        {
            var filename = _isoService.SanitizeFilename(file.Name);

            // Cheap pre-flight check on the client-reported size, plus an authoritative check on the
            // actual uploaded byte count - the form value is client-controlled and must not be trusted.
            if (reportedSize > _isoUploadOptions.MaxFileSize || file.Length > _isoUploadOptions.MaxFileSize)
            {
                throw new BadRequestException($"File exceeds the {_isoUploadOptions.MaxFileSize} byte maximum size.");
            }

            // One or more target folders the ISO is written to (view id for "view" scope, else each
            // selected team id - or the primary team when none were specified). Permissions enforced here.
            var scopeIds = await _isoService.ResolveUploadScopeIdsAsync(viewId, scope, teamIds, ct);

            // The datastore path needs a seekable local file to stream to each host, so it stages
            // first. The NFS path writes straight to its destination(s) instead - staging there would
            // impose a full-size temp-space requirement on deployments that never had one (and that
            // TempStagingPath documents as datastore-only).
            if (_isoUploadOptions.UploadToDatastore)
            {
                return await UploadToDatastoreStaged(viewId, file, filename, scopeIds, ct);
            }

            await UploadToNfs(viewId, file, filename, scopeIds, ct);

            return new IsoUploadResult { Message = "ISO was uploaded" };
        }

        // Datastore path: stage the (possibly converted) ISO locally, then stream it to every target
        // scope. This finally covers a failure during the upload; a failure during staging is cleaned
        // up by StageIsoAsync itself, since tempPath is not assigned here until staging succeeds.
        private async Task<IsoUploadResult> UploadToDatastoreStaged(
            Guid viewId, IFormFile file, string filename, IReadOnlyList<string> scopeIds, CancellationToken ct)
        {
            string tempPath = null;

            try
            {
                (tempPath, var destName) = await StageIsoAsync(file, filename, ct);
                return await UploadToDatastore(viewId.ToString(), scopeIds, destName, tempPath);
            }
            finally
            {
                DeleteIfExists(tempPath);
            }
        }

        // Best-effort removal of a staged temp file. Never throws: a cleanup failure must not mask the
        // outcome (or the exception) of the upload itself.
        private static void DeleteIfExists(string path)
        {
            if (path == null)
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* best-effort cleanup */ }
        }

        // NFS path: write the upload directly into the target folder(s) under BasePath - no local
        // staging. The request stream can only be read once, so the first scope is written from the
        // stream and any remaining scopes are file-copied from it.
        private async Task UploadToNfs(
            Guid viewId, IFormFile file, string filename, IReadOnlyList<string> scopeIds, CancellationToken ct)
        {
            // Same naming as the datastore path: a real ISO keeps its name, anything else is wrapped
            // into an ISO and gains the extension.
            var isIso = IsoFileNaming.IsIsoFile(filename);
            var destName = isIso ? filename : filename + IsoFileNaming.Extension;

            string DestFileFor(string scopeId)
            {
                var destPath = Path.Combine(_isoUploadOptions.BasePath, viewId.ToString(), scopeId);
                Directory.CreateDirectory(destPath);
                return Path.Combine(destPath, destName);
            }

            var firstFile = DestFileFor(scopeIds[0]);

            using (var sourceStream = file.OpenReadStream())
            {
                if (isIso)
                {
                    using var destStream = File.Create(firstFile);
                    await sourceStream.CopyToAsync(destStream, ct);
                }
                else
                {
                    BuildIso(sourceStream, filename, firstFile);
                }
            }

            // Remaining scopes copy from the file just written rather than re-reading the request body.
            await Task.WhenAll(scopeIds.Skip(1).Select(async scopeId =>
            {
                using var source = File.OpenRead(firstFile);
                using var dest = File.Create(DestFileFor(scopeId));
                await source.CopyToAsync(dest, ct);
            }));
        }

        // Resolve the staging directory, normalize the destination name to "*.iso", and stage the
        // upload as an ISO locally. Real ISOs are streamed to disk directly; any other file is wrapped
        // into a single-file ISO. The datastore filename matches the NFS naming so GetIsos/MountIso
        // resolve it identically. The caller owns deleting the returned tempPath - but only once this
        // returns: the caller's variable is unassigned until then, so a failure part-way through the
        // copy/conversion must delete the partial file here or it would be leaked.
        private async Task<(string tempPath, string isoName)> StageIsoAsync(IFormFile formFile, string filename, CancellationToken ct)
        {
            var stagingDir = string.IsNullOrWhiteSpace(_isoUploadOptions.TempStagingPath)
                ? Path.GetTempPath()
                : _isoUploadOptions.TempStagingPath;
            Directory.CreateDirectory(stagingDir);

            var isIso = IsoFileNaming.IsIsoFile(filename);
            var isoName = isIso ? filename : filename + IsoFileNaming.Extension;
            var tempPath = Path.Combine(stagingDir, Guid.NewGuid().ToString() + IsoFileNaming.Extension);

            try
            {
                using (var sourceStream = formFile.OpenReadStream())
                {
                    if (isIso)
                    {
                        using var destStream = File.Create(tempPath);
                        await sourceStream.CopyToAsync(destStream, ct);
                    }
                    else
                    {
                        BuildIso(sourceStream, filename, tempPath);
                    }
                }
            }
            catch
            {
                DeleteIfExists(tempPath);
                throw;
            }

            return (tempPath, isoName);
        }

        // Stream the already-staged ISO directly to the vSphere datastore(s) for each target scope in
        // parallel. Used for VMware Cloud on AWS SDDC environments that lack NFS datastores. Each
        // scope's UploadIso internally fans out across all enabled+connected hosts.
        private async Task<IsoUploadResult> UploadToDatastore(string viewId, IReadOnlyList<string> scopeIds, string isoName, string tempPath)
        {
            var outcomes = await Task.WhenAll(
                scopeIds.Select(scopeId => _vsphereService.UploadIso(viewId, scopeId, isoName, tempPath)));

            // Aggregate per-host counts across every target scope. Detail (which hosts) stays in
            // the logs - the response carries only admin-safe counts.
            var failedHostCount = outcomes.Sum(o => o.FailedHostCount);
            var totalHostCount = outcomes.Sum(o => o.TotalHostCount);

            if (failedHostCount > 0)
            {
                return new IsoUploadResult
                {
                    Message = $"ISO uploaded, but failed on {failedHostCount} of {totalHostCount} hosts. Contact an administrator.",
                    FailedHostCount = failedHostCount,
                    TotalHostCount = totalHostCount
                };
            }

            return new IsoUploadResult
            {
                Message = "ISO was uploaded",
                TotalHostCount = totalHostCount
            };
        }

        // Wrap an arbitrary uploaded file into a single-file ISO at destPath (Joliet, "PlayerIso" volume).
        private static void BuildIso(Stream source, string filename, string destPath)
        {
            CDBuilder builder = new CDBuilder();
            builder.UseJoliet = true;
            builder.VolumeIdentifier = "PlayerIso";
            builder.AddFile(filename, source);
            builder.Build(destPath);
        }
    }
}
