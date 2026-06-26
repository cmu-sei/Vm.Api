// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
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
    // View-scoped ISO listing for the management UI. Returns the view-wide (public) ISOs plus the
    // ISOs for each relevant team: all teams in the View for a caller who can view/manage the whole
    // View, else just the caller's own teams. Listing is a read, so it is gated on view/team read
    // permissions - not the upload permissions (a viewer who can't upload can still list).
    public class ListViewIsos : IFeatureHandler
    {
        private readonly IIsoService _isoService;
        private readonly IPlayerService _playerService;

        public ListViewIsos(
            IIsoService isoService,
            IPlayerService playerService)
        {
            _isoService = isoService;
            _playerService = playerService;
        }

        public async Task<IsoResult> HandleAsync(Guid viewId, CancellationToken ct)
        {
            // Can the caller read the whole View (system view-readers or view-level readers)? They
            // then see every team's ISOs.
            var canViewWholeView = await _playerService.Can(
                [], [viewId],
                [AppSystemPermission.ViewViews, AppSystemPermission.ManageViews],
                [AppViewPermission.ViewView, AppViewPermission.ManageView],
                [], ct);

            if (!canViewWholeView)
            {
                // Caller must at least be able to view a team in this View to see the listing.
                var ownTeamsForCheck = (await _playerService.GetTeamsByViewIdAsync(viewId, ct)).Select(t => t.Id).ToArray();
                if (!await _playerService.Can(ownTeamsForCheck, [], [], [], [AppTeamPermission.ViewTeam, AppTeamPermission.ManageTeam], ct))
                    throw new ForbiddenException("You do not have permission to view ISOs for this View");
            }

            // Whole-View readers see every team; everyone else sees only their own teams.
            var teams = canViewWholeView
                ? (await _playerService.GetAllTeamsByViewIdAsync(viewId, ct)).ToList()
                : (await _playerService.GetTeamsByViewIdAsync(viewId, ct)).ToList();

            var view = await _playerService.GetViewByIdAsync(viewId, ct);

            return await _isoService.BuildViewIsoResultAsync(view, teams, ct);
        }
    }
}
