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
    // ISOs for the primary team and teams visible through its scopes, or every team when the
    // primary-team context has an effective whole-View permission. API authorization still considers
    // every effective claim available to the caller.
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
            var teams = (await _playerService.GetTeamsByViewIdAsync(viewId, ct))?.ToList() ?? [];
            var teamIds = teams.Select(x => x.Id).ToArray();

            var canView = await _playerService.Can(
                teamIds,
                [viewId],
                [],
                [AppViewPermission.ViewView, AppViewPermission.ManageView],
                [AppTeamPermission.ViewTeam, AppTeamPermission.ManageTeam],
                ct);

            if (!canView || teams.Count == 0)
                throw new ForbiddenException("You do not have permission to view ISOs for this View");

            var view = await _playerService.GetViewByIdAsync(viewId, ct);

            var results = await _isoService.BuildViewIsoResultsAsync(new[] { new ViewTeams(view, teams) }, ct);
            return results[0];
        }
    }
}
