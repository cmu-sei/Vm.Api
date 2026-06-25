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
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Features.Files.Requests
{
    // Uploads a file as an ISO to a View's view-wide folder or to one/more team folders. Stages the
    // (possibly converted) ISO once locally, then writes it to every resolved scope - either to the
    // vSphere datastore(s) or to the NFS base path, depending on configuration. The controller owns
    // reading the form; this handler owns the staging + write orchestration.
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

            // Stage the (possibly converted) ISO once locally; both the datastore and NFS paths copy
            // from this single staged file into each target scope.
            var (tempPath, destName) = await StageIsoAsync(file, filename, ct);

            try
            {
                if (_isoUploadOptions.UploadToDatastore)
                {
                    return await UploadToDatastore(viewId.ToString(), scopeIds, destName, tempPath);
                }

                // NFS path: copy the staged ISO into every target folder in parallel.
                await Task.WhenAll(scopeIds.Select(async scopeId =>
                {
                    var destPath = Path.Combine(_isoUploadOptions.BasePath, viewId.ToString(), scopeId);
                    Directory.CreateDirectory(destPath);
                    using var source = File.OpenRead(tempPath);
                    using var dest = File.Create(Path.Combine(destPath, destName));
                    await source.CopyToAsync(dest, ct);
                }));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch { /* best-effort cleanup */ }
                }
            }

            return new IsoUploadResult { Message = "ISO was uploaded" };
        }

        // Resolve the staging directory, normalize the destination name to "*.iso", and stage the
        // upload as an ISO locally. Real ISOs are streamed to disk directly; any other file is wrapped
        // into a single-file ISO. The datastore filename matches the NFS naming so GetIsos/MountIso
        // resolve it identically. The caller owns deleting the returned tempPath.
        private async Task<(string tempPath, string isoName)> StageIsoAsync(IFormFile formFile, string filename, CancellationToken ct)
        {
            var stagingDir = string.IsNullOrWhiteSpace(_isoUploadOptions.TempStagingPath)
                ? Path.GetTempPath()
                : _isoUploadOptions.TempStagingPath;
            Directory.CreateDirectory(stagingDir);

            var isIso = IsoFileNaming.IsIsoFile(filename);
            var isoName = isIso ? filename : filename + IsoFileNaming.Extension;
            var tempPath = Path.Combine(stagingDir, Guid.NewGuid().ToString() + IsoFileNaming.Extension);

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
