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
        public async Task<IActionResult> Upload(Guid uuid)
        {
            var formFile = Request.Form.Files[0];
            var filename = SanitizeFilename(formFile.Name);
            var scope = Request.Form["scope"][0];
            var size = Convert.ToInt64(Request.Form["size"][0]);

            // Cheap pre-flight check on the client-reported size, plus an authoritative check on the
            // actual uploaded byte count - the form value is client-controlled and must not be trusted.
            if (size > _isoUploadOptions.MaxFileSize || formFile.Length > _isoUploadOptions.MaxFileSize)
            {
                throw new Exception($"File exceeds the {_isoUploadOptions.MaxFileSize} byte maximum size.");
            }

            var scopeId = await ResolveScopeId(uuid, scope);

            if (_isoUploadOptions.UploadToDatastore)
            {
                return await UploadToDatastore(uuid.ToString(), scopeId, filename, formFile);
            }

            var destPath = Path.Combine(
                _isoUploadOptions.BasePath,
                uuid.ToString(),
                scopeId
            );

            var destFile = Path.Combine(destPath, filename);

            Directory.CreateDirectory(destPath);

            using (var sourceStream = formFile.OpenReadStream())
            {
                if (filename.ToLower().EndsWith(".iso"))
                {
                    using (var destStream = System.IO.File.Create(destFile))
                    {
                        await sourceStream.CopyToAsync(destStream);
                    }
                }
                else
                {
                    BuildIso(sourceStream, filename, destFile + ".iso");
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

        // Enforces the ISO UPLOAD permissions and returns the scopeId the ISO folder is keyed on:
        // the view id for "view" scope, else the primary team id.
        private async Task<string> ResolveScopeId(Guid uuid, string scope)
        {
            var team = await _playerService.GetPrimaryTeamByViewIdAsync(uuid, new System.Threading.CancellationToken());

            if (scope == "view")
            {
                if (!await _playerService.Can([team.Id], [], [], [AppViewPermission.UploadViewIsos], [], new System.Threading.CancellationToken()))
                    throw new InvalidOperationException("You do not have permission to upload public files for this View");
            }
            else
            {
                if (!await _playerService.Can([team.Id], [], [], [AppViewPermission.UploadViewIsos], [AppTeamPermission.UploadTeamIsos], new System.Threading.CancellationToken()))
                    throw new InvalidOperationException("You do not have permission to upload files for this Team");
            }

            return (scope == "view") ? uuid.ToString() : team.Id.ToString();
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

        // Stage the upload as an ISO locally, then stream it directly to the vSphere datastore(s).
        // Used for VMware Cloud on AWS SDDC environments that lack NFS datastores.
        private async Task<IActionResult> UploadToDatastore(string viewId, string scopeId, string filename, IFormFile formFile)
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

                var outcome = await _vsphereService.UploadIso(viewId, scopeId, isoName, tempPath);

                if (outcome.FailedHostCount > 0)
                {
                    // Generic, admin-safe message - which hosts failed is in the server logs, not here.
                    return Json(new IsoUploadResult
                    {
                        Message = $"ISO uploaded, but failed on {outcome.FailedHostCount} of {outcome.TotalHostCount} hosts. Contact an administrator.",
                        FailedHostCount = outcome.FailedHostCount,
                        TotalHostCount = outcome.TotalHostCount
                    });
                }

                return Json(new IsoUploadResult
                {
                    Message = "ISO was uploaded",
                    TotalHostCount = outcome.TotalHostCount
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
