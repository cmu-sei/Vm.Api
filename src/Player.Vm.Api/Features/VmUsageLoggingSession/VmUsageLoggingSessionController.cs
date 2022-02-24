/*
Crucible
Copyright 2022 Carnegie Mellon University.
NO WARRANTY. THIS CARNEGIE MELLON UNIVERSITY AND SOFTWARE ENGINEERING INSTITUTE MATERIAL IS FURNISHED ON AN "AS-IS" BASIS. CARNEGIE MELLON UNIVERSITY MAKES NO WARRANTIES OF ANY KIND, EITHER EXPRESSED OR IMPLIED, AS TO ANY MATTER INCLUDING, BUT NOT LIMITED TO, WARRANTY OF FITNESS FOR PURPOSE OR MERCHANTABILITY, EXCLUSIVITY, OR RESULTS OBTAINED FROM USE OF THE MATERIAL. CARNEGIE MELLON UNIVERSITY DOES NOT MAKE ANY WARRANTY OF ANY KIND WITH RESPECT TO FREEDOM FROM PATENT, TRADEMARK, OR COPYRIGHT INFRINGEMENT.
Released under a MIT (SEI)-style license, please see license.txt or contact permission@sei.cmu.edu for full terms.
[DISTRIBUTION STATEMENT A] This material has been approved for public release and unlimited distribution.  Please see Copyright notice for non-US Government use and distribution.
Carnegie Mellon(R) and CERT(R) are registered in the U.S. Patent and Trademark Office by Carnegie Mellon University.
DM20-0181
*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Player.Vm.Api.Features.VmUsageLoggingSession
{
    [Route("api/vmusageloggingsessions")]
    [ApiController]
    [AllowAnonymous]
    public class VmUsageLoggingSessionController : ControllerBase
    {
        private readonly IMediator _mediator;

        public VmUsageLoggingSessionController(IMediator mediator)
        {
            _mediator = mediator;
        }

        /// <summary>
        /// Get a single VmUsageLoggingSession.
        /// </summary>
        /// <param name="id">ID of a session.</param>
        /// <returns></returns>
        [HttpGet("{id}")]
        [ProducesResponseType(typeof(VmUsageLoggingSession), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "GetSession")]
        public async Task<IActionResult> Get([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new Get.Query { Id = id });
            return Ok(result);
        }


        /// <summary>
        /// Get all VmUsageLoggingSessions.
        /// </summary>
        /// <returns></returns>
        [HttpGet()]
        [ProducesResponseType(typeof(IEnumerable<VmUsageLoggingSession>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "GetAllSessions")]
        public async Task<IActionResult> GetAll()
        {
            var result = await _mediator.Send(new GetAll.Query());
            return Ok(result);
        }

        /// <summary>
        /// Get active VmUsageLoggingSessions.
        /// </summary>
        /// <returns></returns>
        [HttpGet("active")]
        [ProducesResponseType(typeof(IEnumerable<VmUsageLoggingSession>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "GetActiveSessions")]
        public async Task<IActionResult> GetActive()
        {
            var result = await _mediator.Send(new GetActive.Query());
            return Ok(result);
        }        

        /// <summary>
        /// Get active VmUsageLoggingSessions By TeamId.
        /// </summary>
        /// <returns></returns>
        [HttpGet("activebyteam/{teamId}")]
        [ProducesResponseType(typeof(IEnumerable<VmUsageLoggingSession>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "GetActiveSessionsByTeam")]
        public async Task<IActionResult> GetActiveByTeam([FromRoute] Guid teamId)
        {
            var result = await _mediator.Send(new GetActiveByTeam.Query { TeamId = teamId });
            return Ok(result);
        }   

        /// <summary>
        /// Create a new VmUsageLoggingSession.
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        [HttpPost()]
        [ProducesResponseType(typeof(VmUsageLoggingSession), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "CreateSession")]
        public async Task<IActionResult> Create(Create.Command command)
        {
            var result = await _mediator.Send(command);
            return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
        }

        /// <summary>
        /// Update a VmUsageLoggingSession.
        /// </summary>
        /// <param name="id">ID of a VmUsageLoggingSession.</param>
        /// <param name="command"></param>
        /// <returns></returns>
        [HttpPut("{id}")]
        [ProducesResponseType(typeof(VmUsageLoggingSession), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "EditSession")]
        public async Task<IActionResult> Edit([FromRoute] Guid id, Edit.Command command)
        {
            command.Id = id;
            var result = await _mediator.Send(command);
            return Ok(result);
        }

        /// <summary>
        /// Delete a VmUsageLoggingSession.
        /// </summary>
        /// <param name="id">ID of an VmUsageLoggingSessions.</param>
        /// <returns></returns>
        [HttpDelete("{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "DeleteSession")]
        public async Task<IActionResult> Delete([FromRoute] Guid id)
        {
            await _mediator.Send(new Delete.Command { Id = id });
            return NoContent();
        }

        /// <summary>
        /// End a VmUsageLoggingSession.
        /// </summary>
        /// <param name="id">ID of a VmUsageLoggingSession.</param>
        /// <returns></returns>
        [HttpPost("{id}/endsession")]
        [ProducesResponseType(typeof(VmUsageLoggingSession), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "EndSession")]
        public async Task<IActionResult> EndSession([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new EndSession.Command { Id = id });
            return Ok(result);
        }     

        /// <summary>
        /// Get CSV file for all log entries in a VmUsageLoggingSession.
        /// </summary>
        /// <returns></returns>
        [HttpGet("{id}/download")]
        [Produces("text/csv")]
        [SwaggerOperation(OperationId = "GetCsvFile")]
        public async Task<IActionResult> GetCsvFile([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new GetCsvFile.Query {SessionId = id});
            return result;
        }
    }
}
