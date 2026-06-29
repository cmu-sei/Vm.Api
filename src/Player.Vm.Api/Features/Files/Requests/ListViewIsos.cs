// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Files.Requests
{
    // View-scoped ISO listing for the management UI. Returns the view-wide (public) ISOs plus the
    // ISOs for each relevant team: all teams in the View for a caller who can view/manage the whole
    // View, else just the caller's own primary team. Listing is a read, so it is gated on view/team
    // read permissions - not the upload permissions (a viewer who can't upload can still list).
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
            // Whole-view listing follows the ACTIVE (primary) team: only show every team's ISOs when
            // the caller's primary team can view/manage the whole View. Holding the perm via another
            // team does NOT widen this single-view listing. System perms are intentionally NOT passed
            // here, so a system operator's whole-View/all-teams listing comes from the "all views"
            // admin mode (ListAllIsos), not this active-team-scoped tab.
            var canViewWholeView = await _playerService.Can(
                [], [viewId],
                [],
                [AppViewPermission.ViewView, AppViewPermission.ManageView],
                [], ct, primaryTeamOnly: true);

            // Whole-View readers see every team; everyone else sees only their own primary team.
            IReadOnlyCollection<Team> teams;
            if (canViewWholeView)
            {
                teams = (await _playerService.GetAllTeamsByViewIdAsync(viewId, ct)).ToList();
            }
            else
            {
                // Non-privileged users see only their primary team's ISOs (plus the view-wide ISOs),
                // and only if that team grants read access (ViewTeam/ManageTeam on the primary team).
                var primaryTeam = await _playerService.GetPrimaryTeamByViewIdAsync(viewId, ct);
                if (primaryTeam == null || !await _playerService.Can(
                        [primaryTeam.Id], [viewId], [], [],
                        [AppTeamPermission.ViewTeam, AppTeamPermission.ManageTeam], ct, primaryTeamOnly: true))
                    throw new ForbiddenException("You do not have permission to view ISOs for this View");

                teams = new[] { primaryTeam };
            }

            var view = await _playerService.GetViewByIdAsync(viewId, ct);

            var results = await _isoService.BuildViewIsoResultsAsync(new[] { new ViewTeams(view, teams) }, ct);
            return results[0];
        }
    }
}
