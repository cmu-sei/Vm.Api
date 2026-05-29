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
        [ProducesResponseType((int)HttpStatusCode.OK)]
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

            var scopeId = (scope == "view") ? uuid.ToString() : team.Id.ToString();

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

            return Json("ISO was uploaded");
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

                var failures = await _vsphereService.UploadIso(viewId, scopeId, isoName, tempPath);

                if (failures.Any())
                {
                    return Json($"ISO uploaded, but failed on: {string.Join("; ", failures)}");
                }

                return Json("ISO was uploaded");
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
