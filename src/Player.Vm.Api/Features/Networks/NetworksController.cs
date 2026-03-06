// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
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
        /// Get all networks for a view
        /// </summary>
        [HttpGet("views/{viewId}/networks")]
        [ProducesResponseType(typeof(IEnumerable<ViewNetworkDto>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getViewNetworks")]
        public async Task<IActionResult> GetByViewId([FromRoute] Guid viewId, CancellationToken ct)
        {
            var networks = await _networkService.GetByViewId(viewId, ct);
            return Ok(networks);
        }

        /// <summary>
        /// Get a single network entry for a view
        /// </summary>
        [HttpGet("views/{viewId}/networks/{id}")]
        [ProducesResponseType(typeof(ViewNetworkDto), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getViewNetwork")]
        public async Task<IActionResult> GetById([FromRoute] Guid viewId, [FromRoute] Guid id, CancellationToken ct)
        {
            var network = await _networkService.GetById(viewId, id, ct);
            return Ok(network);
        }

        /// <summary>
        /// Create a network entry for a view
        /// </summary>
        /// <remarks>
        /// Idempotent — if a network with the same ProviderType, ProviderInstanceId, and NetworkId already exists for this view, it is returned.
        /// </remarks>
        [HttpPost("views/{viewId}/networks")]
        [ProducesResponseType(typeof(ViewNetworkDto), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createViewNetwork")]
        public async Task<IActionResult> Create([FromRoute] Guid viewId, [FromBody] CreateViewNetworkForm form, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                throw new InvalidOperationException();

            var network = await _networkService.CreateViewNetwork(viewId, form, ct);
            return CreatedAtAction(nameof(GetById), new { viewId, id = network.Id }, network);
        }

        /// <summary>
        /// Update a network entry for a view
        /// </summary>
        [HttpPut("views/{viewId}/networks/{id}")]
        [ProducesResponseType(typeof(ViewNetworkDto), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateViewNetwork")]
        public async Task<IActionResult> Update([FromRoute] Guid viewId, [FromRoute] Guid id, [FromBody] UpdateViewNetworkForm form, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                throw new InvalidOperationException();

            var network = await _networkService.UpdateViewNetwork(viewId, id, form, ct);
            return Ok(network);
        }

        /// <summary>
        /// Delete a network entry from a view
        /// </summary>
        [HttpDelete("views/{viewId}/networks/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteViewNetwork")]
        public async Task<IActionResult> Delete([FromRoute] Guid viewId, [FromRoute] Guid id, CancellationToken ct)
        {
            await _networkService.DeleteViewNetwork(viewId, id, ct);
            return NoContent();
        }
    }
}
