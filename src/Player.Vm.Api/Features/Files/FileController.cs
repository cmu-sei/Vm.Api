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
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Vsphere.Services;
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

        [HttpDelete("views/{uuid}/isos")]
        [ProducesResponseType(typeof(IsoUploadResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "deleteIso")]
        public async Task<IActionResult> Delete(Guid uuid, [FromQuery] string scope, [FromQuery] string filename, [FromQuery] Guid? teamId = null)
        {
            // Sanitize before the name touches a filesystem/URL path (guards against ../ traversal).
            filename = SanitizeFilename(filename);

            var scopeId = await ResolveScopeId(uuid, scope);

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

        // Enforces the ISO scope permissions (identical for upload and delete) and returns the
        // scopeId the ISO folder is keyed on: the view id for "view" scope, else the primary team id.
        private async Task<string> ResolveScopeId(Guid uuid, string scope)
        {
            var team = await _playerService.GetPrimaryTeamByViewIdAsync(uuid, new System.Threading.CancellationToken());

            if (scope == "view")
            {
                if (!await _playerService.Can([team.Id], [], [], [AppViewPermission.UploadViewIsos], [], new System.Threading.CancellationToken()))
                    throw new InvalidOperationException("You do not have permission to manage public files for this View");
            }
            else
            {
                if (!await _playerService.Can([team.Id], [], [], [AppViewPermission.UploadViewIsos], [AppTeamPermission.UploadTeamIsos], new System.Threading.CancellationToken()))
                    throw new InvalidOperationException("You do not have permission to manage files for this Team");
            }

            return (scope == "view") ? uuid.ToString() : team.Id.ToString();
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
