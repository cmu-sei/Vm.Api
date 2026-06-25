// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Features.Vsphere;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Files.Requests
{
    // System-wide ISO listing for the management UI's "all views" mode. Returns one IsoResult per
    // View in the system (view-wide + every team's ISOs). Gated by a system permission so it can
    // surface Views/teams the caller is not a member of - read access only; deletion is enforced
    // separately by ResolveDeleteScopeId (which requires the DeleteIsos system permission to remove
    // an ISO the caller has no specific Delete*Isos permission for).
    public class ListAllIsos : IFeatureHandler
    {
        private readonly IIsoService _isoService;
        private readonly IPlayerService _playerService;

        public ListAllIsos(
            IIsoService isoService,
            IPlayerService playerService)
        {
            _isoService = isoService;
            _playerService = playerService;
        }

        public async Task<GetIsos.IsoResult[]> HandleAsync(CancellationToken ct)
        {
            if (!await _playerService.Can([], [], [AppSystemPermission.ViewViews, AppSystemPermission.ManageViews], [], [], ct))
                throw new ForbiddenException("You do not have permission to view ISOs across all Views");

            var views = (await _playerService.GetAllViewsAsync(ct)).ToList();

            // This is views * (1 + teams) datastore-browser round-trips over a single pooled vSphere
            // connection, so cap the per-View fan-out to avoid overwhelming it on large systems.
            using var throttle = new System.Threading.SemaphoreSlim(4);

            var results = await Task.WhenAll(views.Select(async view =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    var teams = (await _playerService.GetAllTeamsByViewIdAsync(view.Id, ct)).ToList();
                    return await _isoService.BuildViewIsoResultAsync(view, teams, ct);
                }
                finally
                {
                    throttle.Release();
                }
            }));

            return results;
        }
    }
}
