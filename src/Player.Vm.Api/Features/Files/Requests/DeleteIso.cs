// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Shared.Interfaces;

namespace Player.Vm.Api.Features.Files.Requests
{
    // Deletes a single ISO from a View's view-wide folder or a team folder. Resolves the target scope
    // (enforcing delete permissions); IsoService removes the file from every configured hypervisor.
    public class DeleteIso : IFeatureHandler
    {
        private readonly IIsoService _isoService;
        private readonly IXApiService _xApiService;

        public DeleteIso(IIsoService isoService, IXApiService xApiService)
        {
            _isoService = isoService;
            _xApiService = xApiService;
        }

        public async Task<IsoUploadResult> HandleAsync(Guid viewId, string scope, string filename, Guid? teamId, CancellationToken ct)
        {
            // Sanitize before the name touches a filesystem/URL path (guards against ../ traversal).
            filename = _isoService.SanitizeFilename(filename);

            var scopeId = await _isoService.ResolveDeleteScopeIdAsync(viewId, scope, teamId, ct);

            var result = await _isoService.DeleteAsync(viewId, scopeId, filename, ct);
            await _xApiService.TrackIsoDeletedAsync(viewId, scope, filename, ct);
            return result;
        }
    }
}
