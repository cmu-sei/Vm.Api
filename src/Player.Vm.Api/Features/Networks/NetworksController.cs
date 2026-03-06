// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Vm.Api.Features.Networks
{
    [Authorize]
    [Route("api/")]
    [ApiController]
    public class NetworksController : ControllerBase
    {
        private readonly INetworkService _networkService;

        public NetworksController(INetworkService networkService)
        {
            _networkService = networkService;
        }

        /// <summary>
        /// Get all network permissions for a team
        /// </summary>
        /// <param name="teamId">The Id of the Team</param>
        /// <param name="ct"></param>
        [HttpGet("teams/{teamId}/network-permissions")]
        [ProducesResponseType(typeof(IEnumerable<TeamNetworkPermission>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getTeamNetworkPermissions")]
        public async Task<IActionResult> GetByTeamId([FromRoute] Guid teamId, CancellationToken ct)
        {
            var permissions = await _networkService.GetByTeamId(teamId, ct);
            return Ok(permissions);
        }

        /// <summary>
        /// Get a single network permission by Id
        /// </summary>
        /// <param name="teamId">The Id of the Team</param>
        /// <param name="id">The Id of the network permission</param>
        /// <param name="ct"></param>
        [HttpGet("teams/{teamId}/network-permissions/{id}")]
        [ProducesResponseType(typeof(TeamNetworkPermission), (int)HttpStatusCode.OK)]
        [ProducesResponseType((int)HttpStatusCode.NotFound)]
        [SwaggerOperation(OperationId = "getTeamNetworkPermission")]
        public async Task<IActionResult> Get([FromRoute] Guid teamId, [FromRoute] Guid id, CancellationToken ct)
        {
            var permission = await _networkService.Get(teamId, id, ct);
            return Ok(permission);
        }

        /// <summary>
        /// Add a network permission for a team
        /// </summary>
        /// <remarks>
        /// Idempotent — if a permission with the same ProviderType, ProviderInstanceId, and NetworkId already exists, it is returned.
        /// </remarks>
        /// <param name="teamId">The Id of the Team</param>
        /// <param name="form">The network permission to create</param>
        /// <param name="ct"></param>
        [HttpPost("teams/{teamId}/network-permissions")]
        [ProducesResponseType(typeof(TeamNetworkPermission), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createTeamNetworkPermission")]
        public async Task<IActionResult> Create([FromRoute] Guid teamId, [FromBody] TeamNetworkPermissionForm form, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                throw new InvalidOperationException();

            var permission = await _networkService.Create(teamId, form, ct);
            return CreatedAtAction(nameof(Get), new { teamId, id = permission.Id }, permission);
        }

        /// <summary>
        /// Delete a single network permission
        /// </summary>
        /// <param name="teamId">The Id of the Team</param>
        /// <param name="id">The Id of the network permission</param>
        /// <param name="ct"></param>
        [HttpDelete("teams/{teamId}/network-permissions/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteTeamNetworkPermission")]
        public async Task<IActionResult> Delete([FromRoute] Guid teamId, [FromRoute] Guid id, CancellationToken ct)
        {
            await _networkService.Delete(teamId, id, ct);
            return NoContent();
        }

        /// <summary>
        /// Delete all network permissions for a team
        /// </summary>
        /// <param name="teamId">The Id of the Team</param>
        /// <param name="ct"></param>
        [HttpDelete("teams/{teamId}/network-permissions")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteAllTeamNetworkPermissions")]
        public async Task<IActionResult> DeleteAll([FromRoute] Guid teamId, CancellationToken ct)
        {
            await _networkService.DeleteAllByTeam(teamId, ct);
            return NoContent();
        }
    }
}
