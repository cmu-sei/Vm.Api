// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Player.Vm.Api.Features.Files.Requests;
using Player.Vm.Api.Features.Vsphere;
using Player.Vm.Api.Infrastructure.Exceptions;
using Swashbuckle.AspNetCore.Annotations;

namespace Player.Vm.Api.Features.Files
{
    [Authorize]
    [ApiController]
    [Route("api/")]
    public class FileController : Controller
    {
        [HttpPost("views/{uuid}/isos"), DisableRequestSizeLimit]
        [ProducesResponseType(typeof(IsoUploadResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "uploadFileAsIso")]
        public async Task<IActionResult> Upload(Guid uuid,
            [FromServices] IIsoService isoService, [FromServices] UploadIso uploadIso, CancellationToken ct)
        {
            if (Request.Form.Files.Count == 0)
            {
                throw new BadRequestException("A file is required.");
            }

            var formFile = Request.Form.Files[0];

            var scope = Request.Form["scope"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(scope))
            {
                throw new BadRequestException("A scope is required.");
            }

            if (!long.TryParse(Request.Form["size"].FirstOrDefault(), out var size))
            {
                throw new BadRequestException("A valid size is required.");
            }

            var teamIds = isoService.ParseTeamIds(Request.Form["teamIds"]);

            return Json(await uploadIso.HandleAsync(uuid, formFile, scope, size, teamIds, ct));
        }

        [HttpGet("views/{uuid}/isos")]
        [ProducesResponseType(typeof(ManagedIsoResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getViewIsos")]
        public async Task<IActionResult> List(Guid uuid,
            [FromServices] ListViewIsos listViewIsos, CancellationToken ct)
            => Json(await listViewIsos.HandleAsync(uuid, ct));

        [HttpGet("isos")]
        [ProducesResponseType(typeof(ManagedIsoResult[]), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getAllIsos")]
        public async Task<IActionResult> ListAll(
            [FromServices] ListAllIsos listAllIsos, CancellationToken ct)
            => Json(await listAllIsos.HandleAsync(ct));

        [HttpDelete("views/{uuid}/isos")]
        [ProducesResponseType(typeof(IsoUploadResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "deleteIso")]
        public async Task<IActionResult> Delete(Guid uuid, [FromQuery] string scope,
            [FromQuery] string filename, [FromQuery] Guid? teamId,
            [FromServices] DeleteIso deleteIso, CancellationToken ct = default)
            => Json(await deleteIso.HandleAsync(uuid, scope, filename, teamId, ct));
    }
}
