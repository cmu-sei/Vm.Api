// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Features.Files.Requests
{
    // Uploads a file as an ISO to a View's view-wide folder or to one/more team folders. The controller
    // owns reading the form and this handler owns size limits and scope resolution; IsoService owns
    // where the bytes actually go, since that depends on which hypervisors are configured and how.
    public class UploadIso : IFeatureHandler
    {
        private readonly IIsoService _isoService;
        private readonly IsoUploadOptions _isoUploadOptions;
        private readonly IXApiService _xApiService;

        public UploadIso(
            IIsoService isoService,
            IsoUploadOptions isoUploadOptions,
            IXApiService xApiService)
        {
            _isoService = isoService;
            _isoUploadOptions = isoUploadOptions;
            _xApiService = xApiService;
        }

        public async Task<IsoUploadResult> HandleAsync(Guid viewId, IFormFile file, string scope, long reportedSize, IReadOnlyList<Guid> teamIds, CancellationToken ct)
        {
            var filename = _isoService.SanitizeFilename(
                string.IsNullOrWhiteSpace(file.FileName) ? file.Name : file.FileName);

            // Cheap pre-flight check on the client-reported size, plus an authoritative check on the
            // actual uploaded byte count - the form value is client-controlled and must not be trusted.
            if (reportedSize > _isoUploadOptions.MaxFileSize || file.Length > _isoUploadOptions.MaxFileSize)
            {
                throw new BadRequestException($"File exceeds the {_isoUploadOptions.MaxFileSize} byte maximum size.");
            }

            // One or more target folders the ISO is written to (view id for "view" scope, else each
            // selected team id - or the primary team when none were specified). Permissions enforced here.
            var scopeIds = await _isoService.ResolveUploadScopeIdsAsync(viewId, scope, teamIds, ct);

            var result = await _isoService.UploadAsync(viewId, scopeIds, filename, file.OpenReadStream, ct);
            await _xApiService.TrackIsoUploadedAsync(viewId, scope, filename, ct);
            return result;
        }
    }
}
