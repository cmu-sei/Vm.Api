// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Primitives;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vsphere;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Files
{
    // Shared ISO logic used by more than one of the Files request handlers: permission/scope
    // resolution for upload and delete, assembling the per-View listing, plus the pure filename/
    // teamId parsing helpers. Per-endpoint orchestration lives in the Requests/* handlers.
    public interface IIsoService
    {
        Task<IReadOnlyList<string>> ResolveUploadScopeIdsAsync(Guid viewId, string scope, IReadOnlyList<Guid> teamIds, CancellationToken ct);
        Task<string> ResolveDeleteScopeIdAsync(Guid viewId, string scope, Guid? teamId, CancellationToken ct);
        Task<IsoResult> BuildViewIsoResultAsync(View view, IReadOnlyCollection<Team> teams, CancellationToken ct);
        Task<IsoResult[]> BuildViewIsoResultsAsync(IReadOnlyCollection<(View view, IReadOnlyCollection<Team> teams)> views, CancellationToken ct);
        string SanitizeFilename(string filename);
        IReadOnlyList<Guid> ParseTeamIds(StringValues values);
    }

    public class IsoService : IIsoService
    {
        private readonly IPlayerService _playerService;
        private readonly IVsphereService _vsphereService;

        public IsoService(
            IPlayerService playerService,
            IVsphereService vsphereService)
        {
            _playerService = playerService;
            _vsphereService = vsphereService;
        }

        // Enforces the ISO UPLOAD permissions and returns the scopeId(s) the ISO folder(s) are keyed on:
        //  - "view" scope: requires UploadViewIsos; a single scopeId of the view id.
        //  - "team" scope: targets are the given teamIds (each validated to belong to the View) or the
        //    primary team when none are supplied. Each target requires UploadViewIsos (any team) or
        //    UploadTeamIsos on that team. scopeIds are the team ids.
        public async Task<IReadOnlyList<string>> ResolveUploadScopeIdsAsync(Guid viewId, string scope, IReadOnlyList<Guid> teamIds, CancellationToken ct)
        {
            if (scope == "view")
            {
                if (!await _playerService.Can([], [viewId], [], [AppViewPermission.UploadViewIsos], [], ct))
                    throw new ForbiddenException("You do not have permission to upload public files for this View");

                return new[] { viewId.ToString() };
            }

            // Resolve the target team(s): explicit teamIds (each must belong to the View) else the primary team.
            List<Guid> targetTeamIds;
            if (teamIds.Count > 0)
            {
                // The caller's own teams (all teams for a view-admin). Using this instead of the
                // view-wide team list avoids a privileged GetViewTeams call that 403s for team-only
                // users; the per-team Can(...) check below remains the authoritative permission gate.
                var viewTeamIds = (await _playerService.GetTeamsByViewIdAsync(viewId, ct)).Select(t => t.Id).ToHashSet();
                foreach (var teamId in teamIds)
                {
                    if (!viewTeamIds.Contains(teamId))
                        throw new BadRequestException("The specified team is not part of this View");
                }
                targetTeamIds = teamIds.ToList();
            }
            else
            {
                var primaryTeam = await _playerService.GetPrimaryTeamByViewIdAsync(viewId, ct);
                targetTeamIds = new List<Guid> { primaryTeam.Id };
            }

            // UploadViewIsos lets a view-admin upload to any team; otherwise UploadTeamIsos on the
            // target team is required. Checked per team so a partially-permitted selection is rejected.
            foreach (var targetTeamId in targetTeamIds)
            {
                if (!await _playerService.Can([targetTeamId], [viewId], [], [AppViewPermission.UploadViewIsos], [AppTeamPermission.UploadTeamIsos], ct))
                    throw new ForbiddenException("You do not have permission to upload files for this Team");
            }

            return targetTeamIds.Select(id => id.ToString()).ToList();
        }

        // Enforces the ISO DELETE permissions and returns the scopeId to delete from.
        //  - "view" scope: requires DeleteViewIsos; scopeId is the view id.
        //  - "team" scope: target team is teamId (validated to belong to the View) or the primary team
        //    when teamId is absent. Allowed if the caller has DeleteViewIsos (any team) or DeleteTeamIsos
        //    on that team. scopeId is the team id.
        // The system-level DeleteIsos permission additionally authorizes deleting an ISO in ANY
        // View/team - including ones the caller is not a member of (the "all views" management mode).
        public async Task<string> ResolveDeleteScopeIdAsync(Guid viewId, string scope, Guid? teamId, CancellationToken ct)
        {
            // DeleteIsos is the only permission that lets a caller delete an ISO they have no specific
            // Delete*Isos permission for; checked up front so it can short-circuit the per-scope checks.
            var hasSystemDeleteIsos = await _playerService.Can([], [], [AppSystemPermission.DeleteIsos], [], [], ct);

            if (scope == "view")
            {
                if (hasSystemDeleteIsos)
                    return viewId.ToString();

                if (!await _playerService.Can([], [viewId], [], [AppViewPermission.DeleteViewIsos], [], ct))
                    throw new ForbiddenException("You do not have permission to delete public files for this View");

                return viewId.ToString();
            }

            // Resolve the target team: an explicit teamId (must belong to the View) else the primary team.
            Guid targetTeamId;
            if (teamId.HasValue)
            {
                // Validate the team belongs to the View. A system DeleteIsos caller is generally not a
                // member, so validate against ALL teams in the View; otherwise the caller's own teams
                // (which also avoids a privileged GetViewTeams call that 403s for team-only users).
                var viewTeamIds = hasSystemDeleteIsos
                    ? (await _playerService.GetAllTeamsByViewIdAsync(viewId, ct)).Select(t => t.Id).ToHashSet()
                    : (await _playerService.GetTeamsByViewIdAsync(viewId, ct)).Select(t => t.Id).ToHashSet();
                if (!viewTeamIds.Contains(teamId.Value))
                    throw new BadRequestException("The specified team is not part of this View");

                targetTeamId = teamId.Value;
            }
            else
            {
                var primaryTeam = await _playerService.GetPrimaryTeamByViewIdAsync(viewId, ct);
                targetTeamId = primaryTeam.Id;
            }

            if (hasSystemDeleteIsos)
                return targetTeamId.ToString();

            // DeleteViewIsos lets a view-admin delete any team's ISO; otherwise DeleteTeamIsos on the
            // target team is required.
            if (!await _playerService.Can([targetTeamId], [viewId], [], [AppViewPermission.DeleteViewIsos], [AppTeamPermission.DeleteTeamIsos], ct))
                throw new ForbiddenException("You do not have permission to delete files for this Team");

            return targetTeamId.ToString();
        }

        // Assemble the view-wide + per-team ISO listing for a single View. View-wide ISOs are keyed on
        // the view id; each team's on the team id. A single recursive datastore-browser task enumerates
        // every scope under the View folder.
        public async Task<IsoResult> BuildViewIsoResultAsync(View view, IReadOnlyCollection<Team> teams, CancellationToken ct)
        {
            var isosByScope = await _vsphereService.GetIsosByScopeForView(view.Id.ToString());
            return AssembleViewIsoResult(view, teams, isosByScope);
        }

        // Same as BuildViewIsoResultAsync but for many Views. Each View is enumerated by its own single
        // recursive datastore-browser task; the tasks run concurrently (no per-team fan-out, so no
        // throttling needed).
        public async Task<IsoResult[]> BuildViewIsoResultsAsync(IReadOnlyCollection<(View view, IReadOnlyCollection<Team> teams)> views, CancellationToken ct)
        {
            var tasks = views.Select(async pair =>
            {
                var isosByScope = await _vsphereService.GetIsosByScopeForView(pair.view.Id.ToString());
                return AssembleViewIsoResult(pair.view, pair.teams, isosByScope);
            });

            return await Task.WhenAll(tasks);
        }

        // Bucket the scope-keyed ISO listing into the view-wide + per-team shape. View-wide ISOs are
        // keyed on the view id; each team's on the team id. Scopes with no ISOs yield empty arrays.
        private static IsoResult AssembleViewIsoResult(View view, IReadOnlyCollection<Team> teams, IReadOnlyDictionary<string, List<IsoFile>> isosByScope)
        {
            IsoFile[] IsosFor(string scopeId) =>
                isosByScope.TryGetValue(scopeId, out var isos) ? isos.ToArray() : Array.Empty<IsoFile>();

            var result = new IsoResult
            {
                ViewId = view.Id,
                ViewName = view.Name,
                Isos = IsosFor(view.Id.ToString())
            };

            foreach (var team in teams)
            {
                result.TeamIsoResults.Add(new TeamIsoResult
                {
                    TeamId = team.Id,
                    TeamName = team.Name,
                    Isos = IsosFor(team.Id.ToString())
                });
            }

            return result;
        }

        public string SanitizeFilename(string filename)
        {
            string fn = "";
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in filename.ToCharArray())
                if (!bad.Contains(c))
                    fn += c;
            return fn;
        }

        // Parse the optional "teamIds" form field, which may arrive as repeated values and/or
        // comma-separated lists. Invalid/empty entries are ignored.
        public IReadOnlyList<Guid> ParseTeamIds(StringValues values)
        {
            var ids = new List<Guid>();
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Guid.TryParse(part, out var id))
                        ids.Add(id);
                }
            }
            return ids.Distinct().ToList();
        }
    }
}
