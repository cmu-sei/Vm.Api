// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DiscUtils.Iso9660;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vsphere;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Options;
using Swashbuckle.AspNetCore.Annotations;

namespace Player.Vm.Api.Features.Files
{
    [Authorize]
    [ApiController]
    [Route("api/")]
    public class FileController : Controller
    {
        private IsoUploadOptions _isoUploadOptions;
        private readonly IPlayerService _playerService;
        private readonly IVsphereService _vsphereService;

        public FileController(
            IsoUploadOptions isoUploadOptions,
            IPlayerService playerService,
            IVsphereService vsphereService
        ) : base()
        {
            _isoUploadOptions = isoUploadOptions;
            _playerService = playerService;
            _vsphereService = vsphereService;
        }

        [HttpPost("views/{uuid}/isos"), DisableRequestSizeLimit]
        [ProducesResponseType(typeof(IsoUploadResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "uploadFileAsIso")]
        public async Task<IActionResult> Upload(Guid uuid, CancellationToken ct)
        {
            var formFile = Request.Form.Files[0];
            var filename = SanitizeFilename(formFile.Name);
            var scope = Request.Form["scope"][0];
            var size = Convert.ToInt64(Request.Form["size"][0]);
            var teamIds = ParseTeamIds(Request.Form["teamIds"]);

            // Cheap pre-flight check on the client-reported size, plus an authoritative check on the
            // actual uploaded byte count - the form value is client-controlled and must not be trusted.
            if (size > _isoUploadOptions.MaxFileSize || formFile.Length > _isoUploadOptions.MaxFileSize)
            {
                throw new Exception($"File exceeds the {_isoUploadOptions.MaxFileSize} byte maximum size.");
            }

            // One or more target folders the ISO is written to (view id for "view" scope, else each
            // selected team id - or the primary team when none were specified). Permissions enforced here.
            var scopeIds = await ResolveUploadScopeIds(uuid, scope, teamIds, ct);

            if (_isoUploadOptions.UploadToDatastore)
            {
                return await UploadToDatastore(uuid.ToString(), scopeIds, filename, formFile);
            }

            // Stage the (possibly converted) ISO once, then copy it into every target folder in parallel.
            var isIso = filename.ToLower().EndsWith(".iso");
            var destName = isIso ? filename : filename + ".iso";

            var stagingDir = string.IsNullOrWhiteSpace(_isoUploadOptions.TempStagingPath)
                ? Path.GetTempPath()
                : _isoUploadOptions.TempStagingPath;
            Directory.CreateDirectory(stagingDir);
            var tempPath = Path.Combine(stagingDir, Guid.NewGuid().ToString() + ".iso");

            try
            {
                using (var sourceStream = formFile.OpenReadStream())
                {
                    if (isIso)
                    {
                        using (var destStream = System.IO.File.Create(tempPath))
                        {
                            await sourceStream.CopyToAsync(destStream, ct);
                        }
                    }
                    else
                    {
                        BuildIso(sourceStream, filename, tempPath);
                    }
                }

                await Task.WhenAll(scopeIds.Select(async scopeId =>
                {
                    var destPath = Path.Combine(_isoUploadOptions.BasePath, uuid.ToString(), scopeId);
                    Directory.CreateDirectory(destPath);
                    using var source = System.IO.File.OpenRead(tempPath);
                    using var dest = System.IO.File.Create(Path.Combine(destPath, destName));
                    await source.CopyToAsync(dest, ct);
                }));
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); }
                    catch { /* best-effort cleanup */ }
                }
            }

            return Json(new IsoUploadResult { Message = "ISO was uploaded" });
        }

        // View-scoped ISO listing for the management UI. Returns the view-wide (public) ISOs plus the
        // ISOs for each relevant team: all teams in the View for a view-admin (UploadViewIsos), else
        // just the caller's own teams.
        [HttpGet("views/{uuid}/isos")]
        [ProducesResponseType(typeof(GetIsos.IsoResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getViewIsos")]
        public async Task<IActionResult> List(Guid uuid, CancellationToken ct)
        {
            var canManageView = await _playerService.Can([], [uuid], [], [AppViewPermission.UploadViewIsos], [], ct);

            if (!canManageView)
            {
                // Caller must at least be able to upload team ISOs in this View to see the listing.
                var ownTeamsForCheck = (await _playerService.GetTeamsByViewIdAsync(uuid, ct)).Select(t => t.Id).ToArray();
                if (!await _playerService.Can(ownTeamsForCheck, [], [], [], [AppTeamPermission.UploadTeamIsos], ct))
                    throw new InvalidOperationException("You do not have permission to view ISOs for this View");
            }

            // View-admins see every team; everyone else sees only their own teams.
            var teams = canManageView
                ? (await _playerService.GetAllTeamsByViewIdAsync(uuid, ct)).ToList()
                : (await _playerService.GetTeamsByViewIdAsync(uuid, ct)).ToList();

            var view = await _playerService.GetViewByIdAsync(uuid, ct);

            // View-wide ISOs (scopeId == viewId) plus one task per team scope, run concurrently.
            var viewIsosTask = _vsphereService.GetIsosForScope(uuid.ToString(), uuid.ToString());
            var teamIsoTasks = teams.ToDictionary(
                team => team,
                team => _vsphereService.GetIsosForScope(uuid.ToString(), team.Id.ToString()));

            await Task.WhenAll(new List<Task> { viewIsosTask }.Concat(teamIsoTasks.Values));

            var result = new GetIsos.IsoResult
            {
                ViewId = view.Id,
                ViewName = view.Name,
                Isos = viewIsosTask.Result.ToArray()
            };

            foreach (var kvp in teamIsoTasks)
            {
                result.TeamIsoResults.Add(new GetIsos.TeamIsoResult
                {
                    TeamId = kvp.Key.Id,
                    TeamName = kvp.Key.Name,
                    Isos = kvp.Value.Result.ToArray()
                });
            }

            return Json(result);
        }

        [HttpDelete("views/{uuid}/isos")]
        [ProducesResponseType(typeof(IsoUploadResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "deleteIso")]
        public async Task<IActionResult> Delete(Guid uuid, [FromQuery] string scope, [FromQuery] string filename, [FromQuery] Guid? teamId = null)
        {
            // Sanitize before the name touches a filesystem/URL path (guards against ../ traversal).
            filename = SanitizeFilename(filename);

            var scopeId = await ResolveDeleteScopeId(uuid, scope, teamId);

            if (_isoUploadOptions.UploadToDatastore)
            {
                var outcome = await _vsphereService.DeleteIso(uuid.ToString(), scopeId, filename);

                if (outcome.FailedHostCount > 0)
                {
                    // Generic, admin-safe message - which hosts failed is in the server logs, not here.
                    return Json(new IsoUploadResult
                    {
                        Message = $"ISO deleted, but failed on {outcome.FailedHostCount} of {outcome.TotalHostCount} hosts. Contact an administrator.",
                        FailedHostCount = outcome.FailedHostCount,
                        TotalHostCount = outcome.TotalHostCount
                    });
                }

                return Json(new IsoUploadResult
                {
                    Message = "ISO was deleted",
                    TotalHostCount = outcome.TotalHostCount
                });
            }

            // NFS path: best-effort delete; a missing file is treated as success (idempotent).
            var destFile = Path.Combine(
                _isoUploadOptions.BasePath,
                uuid.ToString(),
                scopeId,
                filename
            );

            if (System.IO.File.Exists(destFile))
            {
                System.IO.File.Delete(destFile);
            }

            return Json(new IsoUploadResult { Message = "ISO was deleted" });
        }

        // Parse the optional "teamIds" form field, which may arrive as repeated values and/or
        // comma-separated lists. Invalid/empty entries are ignored.
        private static List<Guid> ParseTeamIds(Microsoft.Extensions.Primitives.StringValues values)
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

        // Enforces the ISO UPLOAD permissions and returns the scopeId(s) the ISO folder(s) are keyed on:
        //  - "view" scope: requires UploadViewIsos; a single scopeId of the view id.
        //  - "team" scope: targets are the given teamIds (each validated to belong to the View) or the
        //    primary team when none are supplied. Each target requires UploadViewIsos (any team) or
        //    UploadTeamIsos on that team. scopeIds are the team ids.
        private async Task<IReadOnlyList<string>> ResolveUploadScopeIds(Guid uuid, string scope, IReadOnlyList<Guid> teamIds, CancellationToken ct)
        {
            if (scope == "view")
            {
                if (!await _playerService.Can([], [uuid], [], [AppViewPermission.UploadViewIsos], [], ct))
                    throw new InvalidOperationException("You do not have permission to upload public files for this View");

                return new[] { uuid.ToString() };
            }

            // Resolve the target team(s): explicit teamIds (each must belong to the View) else the primary team.
            List<Guid> targetTeamIds;
            if (teamIds.Count > 0)
            {
                var viewTeamIds = (await _playerService.GetAllTeamsByViewIdAsync(uuid, ct)).Select(t => t.Id).ToHashSet();
                foreach (var teamId in teamIds)
                {
                    if (!viewTeamIds.Contains(teamId))
                        throw new InvalidOperationException("The specified team is not part of this View");
                }
                targetTeamIds = teamIds.ToList();
            }
            else
            {
                var primaryTeam = await _playerService.GetPrimaryTeamByViewIdAsync(uuid, ct);
                targetTeamIds = new List<Guid> { primaryTeam.Id };
            }

            // UploadViewIsos lets a view-admin upload to any team; otherwise UploadTeamIsos on the
            // target team is required. Checked per team so a partially-permitted selection is rejected.
            foreach (var targetTeamId in targetTeamIds)
            {
                if (!await _playerService.Can([targetTeamId], [uuid], [], [AppViewPermission.UploadViewIsos], [AppTeamPermission.UploadTeamIsos], ct))
                    throw new InvalidOperationException("You do not have permission to upload files for this Team");
            }

            return targetTeamIds.Select(id => id.ToString()).ToList();
        }

        // Enforces the ISO DELETE permissions and returns the scopeId to delete from.
        //  - "view" scope: requires DeleteViewIsos; scopeId is the view id.
        //  - "team" scope: target team is teamId (validated to belong to the View) or the primary team
        //    when teamId is absent. Allowed if the caller has DeleteViewIsos (any team) or DeleteTeamIsos
        //    on that team. scopeId is the team id.
        private async Task<string> ResolveDeleteScopeId(Guid uuid, string scope, Guid? teamId)
        {
            var ct = new System.Threading.CancellationToken();

            if (scope == "view")
            {
                if (!await _playerService.Can([], [uuid], [], [AppViewPermission.DeleteViewIsos], [], ct))
                    throw new InvalidOperationException("You do not have permission to delete public files for this View");

                return uuid.ToString();
            }

            // Resolve the target team: an explicit teamId (must belong to the View) else the primary team.
            Guid targetTeamId;
            if (teamId.HasValue)
            {
                var viewTeamIds = (await _playerService.GetAllTeamsByViewIdAsync(uuid, ct)).Select(t => t.Id).ToHashSet();
                if (!viewTeamIds.Contains(teamId.Value))
                    throw new InvalidOperationException("The specified team is not part of this View");

                targetTeamId = teamId.Value;
            }
            else
            {
                var primaryTeam = await _playerService.GetPrimaryTeamByViewIdAsync(uuid, ct);
                targetTeamId = primaryTeam.Id;
            }

            // DeleteViewIsos lets a view-admin delete any team's ISO; otherwise DeleteTeamIsos on the
            // target team is required.
            if (!await _playerService.Can([targetTeamId], [uuid], [], [AppViewPermission.DeleteViewIsos], [AppTeamPermission.DeleteTeamIsos], ct))
                throw new InvalidOperationException("You do not have permission to delete files for this Team");

            return targetTeamId.ToString();
        }

        // Stage the upload as an ISO locally, then stream it directly to the vSphere datastore(s) for
        // each target scope in parallel. Used for VMware Cloud on AWS SDDC environments that lack NFS
        // datastores. Each scope's UploadIso internally fans out across all enabled+connected hosts.
        private async Task<IActionResult> UploadToDatastore(string viewId, IReadOnlyList<string> scopeIds, string filename, IFormFile formFile)
        {
            var stagingDir = string.IsNullOrWhiteSpace(_isoUploadOptions.TempStagingPath)
                ? Path.GetTempPath()
                : _isoUploadOptions.TempStagingPath;
            Directory.CreateDirectory(stagingDir);

            var isIso = filename.ToLower().EndsWith(".iso");
            // datastore filename matches the NFS naming so GetIsos/MountIso resolve it identically
            var isoName = isIso ? filename : filename + ".iso";
            var tempPath = Path.Combine(stagingDir, Guid.NewGuid().ToString() + ".iso");

            try
            {
                using (var sourceStream = formFile.OpenReadStream())
                {
                    if (isIso)
                    {
                        using (var destStream = System.IO.File.Create(tempPath))
                        {
                            await sourceStream.CopyToAsync(destStream);
                        }
                    }
                    else
                    {
                        BuildIso(sourceStream, filename, tempPath);
                    }
                }

                var outcomes = await Task.WhenAll(
                    scopeIds.Select(scopeId => _vsphereService.UploadIso(viewId, scopeId, isoName, tempPath)));

                // Aggregate per-host counts across every target scope. Detail (which hosts) stays in
                // the logs - the response carries only admin-safe counts.
                var failedHostCount = outcomes.Sum(o => o.FailedHostCount);
                var totalHostCount = outcomes.Sum(o => o.TotalHostCount);

                if (failedHostCount > 0)
                {
                    return Json(new IsoUploadResult
                    {
                        Message = $"ISO uploaded, but failed on {failedHostCount} of {totalHostCount} hosts. Contact an administrator.",
                        FailedHostCount = failedHostCount,
                        TotalHostCount = totalHostCount
                    });
                }

                return Json(new IsoUploadResult
                {
                    Message = "ISO was uploaded",
                    TotalHostCount = totalHostCount
                });
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); }
                    catch { /* best-effort cleanup */ }
                }
            }
        }

        // Wrap an arbitrary uploaded file into a single-file ISO at destPath (Joliet, "PlayerIso" volume).
        private static void BuildIso(System.IO.Stream source, string filename, string destPath)
        {
            CDBuilder builder = new CDBuilder();
            builder.UseJoliet = true;
            builder.VolumeIdentifier = "PlayerIso";
            builder.AddFile(filename, source);
            builder.Build(destPath);
        }

        private string SanitizeFilename(string filename)
        {
            string fn = "";
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in filename.ToCharArray())
                if (!bad.Contains(c))
                    fn += c;
            return fn;
        }
    }
}
