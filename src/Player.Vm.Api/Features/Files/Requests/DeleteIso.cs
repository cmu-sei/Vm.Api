// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Features.Files.Requests
{
    // Deletes a single ISO from a View's view-wide folder or a team folder. Resolves the target scope
    // (enforcing delete permissions) then removes the file from the vSphere datastore(s) or the NFS
    // base path, depending on configuration.
    public class DeleteIso : IFeatureHandler
    {
        private readonly IIsoService _isoService;
        private readonly IVsphereService _vsphereService;
        private readonly IsoUploadOptions _isoUploadOptions;

        public DeleteIso(
            IIsoService isoService,
            IVsphereService vsphereService,
            IsoUploadOptions isoUploadOptions)
        {
            _isoService = isoService;
            _vsphereService = vsphereService;
            _isoUploadOptions = isoUploadOptions;
        }

        public async Task<IsoUploadResult> HandleAsync(Guid viewId, string scope, string filename, Guid? teamId, CancellationToken ct)
        {
            // Sanitize before the name touches a filesystem/URL path (guards against ../ traversal).
            filename = _isoService.SanitizeFilename(filename);

            var scopeId = await _isoService.ResolveDeleteScopeIdAsync(viewId, scope, teamId, ct);

            if (_isoUploadOptions.UploadToDatastore)
            {
                var outcome = await _vsphereService.DeleteIso(viewId.ToString(), scopeId, filename);

                if (outcome.FailedHostCount > 0)
                {
                    // Generic, admin-safe message - which hosts failed is in the server logs, not here.
                    return new IsoUploadResult
                    {
                        Message = $"ISO deleted, but failed on {outcome.FailedHostCount} of {outcome.TotalHostCount} hosts. Contact an administrator.",
                        FailedHostCount = outcome.FailedHostCount,
                        TotalHostCount = outcome.TotalHostCount
                    };
                }

                return new IsoUploadResult
                {
                    Message = "ISO was deleted",
                    TotalHostCount = outcome.TotalHostCount
                };
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

            return new IsoUploadResult { Message = "ISO was deleted" };
        }
    }
}
