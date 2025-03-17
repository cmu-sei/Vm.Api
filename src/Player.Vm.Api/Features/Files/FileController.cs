// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DiscUtils.Iso9660;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Player.Vm.Api.Domain.Services;
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

        public FileController(
            IsoUploadOptions isoUploadOptions,
            IPlayerService playerService
        ) : base()
        {
            _isoUploadOptions = isoUploadOptions;
            _playerService = playerService;
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

            if (size > _isoUploadOptions.MaxFileSize)
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

            var destPath = Path.Combine(
                _isoUploadOptions.BasePath,
                uuid.ToString(),
                (scope == "view") ? uuid.ToString() : team.Id.ToString()
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
                    CDBuilder builder = new CDBuilder();
                    builder.UseJoliet = true;
                    builder.VolumeIdentifier = "PlayerIso";
                    builder.AddFile(filename, sourceStream);
                    builder.Build(destFile + ".iso");
                }
            }

            return Json("ISO was uploaded");
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
