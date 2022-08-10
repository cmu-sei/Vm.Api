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
using Player.Vm.Api.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Player.Vm.Api.Features.VmUsageLoggingSession
{
    [Route("api/vmusageloggingsessions")]
    [ApiController]
    [AllowAnonymous]
    public class VmUsageLoggingSessionController : ControllerBase
    {
        private readonly IMediator _mediator;
        private VmUsageLoggingOptions _options;

        public VmUsageLoggingSessionController(IMediator mediator, IOptionsMonitor<VmUsageLoggingOptions> vsphereOptionsMonitor)
        {
            _mediator = mediator;
            _options = vsphereOptionsMonitor.CurrentValue;
        }



        /// <summary>
        /// Get a bool value to determine if VM Usage Logging is available.
        /// </summary>
        /// <returns></returns>
        [HttpGet("isloggingenabled")]
        [ProducesResponseType(typeof(bool), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "GetIsLoggingEnabled")]
        public IActionResult GetIsLoggingEnabled()
        {
            return Ok(_options.Enabled);
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
            if (_options.Enabled)
            {
                var result = await _mediator.Send(new Get.Query { Id = id });
                return Ok(result);
            }
            else
            {
                return NotFound("Vm Usage Logging is disabled");
            }
        }


        /// <summary>
        /// Get all VmUsageLoggingSessions.
        /// </summary>
        /// <param name="viewId"></param>
        /// <param name="onlyActive"></param>
        /// <returns></returns>
        [HttpGet()]
        [ProducesResponseType(typeof(IEnumerable<VmUsageLoggingSession>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "GetAllSessions")]
        public async Task<IActionResult> GetAll(Guid? viewId, bool? onlyActive)
        {
            if (_options.Enabled)
            {
                var result = await _mediator.Send(
                    new GetAll.Query {
                        OnlyActive = onlyActive.HasValue ? onlyActive.Value : false,
                        ViewId = viewId.HasValue ? viewId.Value : null
                        });

                return Ok(result);
            }
            else
            {
                return NotFound("Vm Usage Logging is disabled");
            }
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
            if (_options.Enabled)
            {
                var result = await _mediator.Send(command);
                return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
            }
            else
            {
                return NotFound("Vm Usage Logging is disabled");
            }
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
            if (_options.Enabled)
            {
                command.Id = id;
                var result = await _mediator.Send(command);
                return Ok(result);
            }
            else
            {
                return NotFound("Vm Usage Logging is disabled");
            }
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
            if (_options.Enabled)
            {
                await _mediator.Send(new Delete.Command { Id = id });
                return NoContent();
            }
            else
            {
                return NotFound("Vm Usage Logging is disabled");
            }
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
            if (_options.Enabled)
            {
                var result = await _mediator.Send(new EndSession.Command { Id = id });
                return Ok(result);
            }
            else
            {
                return NotFound("Vm Usage Logging is disabled");
            }
        }

        /// <summary>
        /// Get CSV file for all log entries in a VmUsageLoggingSession.
        /// </summary>
        /// <returns></returns>
        [HttpGet("{id}/download")]
        [Produces("text/csv")]
        [SwaggerOperation(OperationId = "GetVmUsageCsvFile")]
        public async Task<IActionResult> GetVmUsageCsvFile([FromRoute] Guid id)
        {
            if (_options.Enabled)
            {
                var result = await _mediator.Send(new GetVmUsageCsvFile.Query {SessionId = id});
                return result;
            }
            else
            {
                return NotFound("Vm Usage Logging is disabled");
            }
        }

        /// <summary>
        /// Get VM Usage Report for a timespan.
        /// </summary>
        /// <param name="reportStart">The start date/time for the report.</param>
        /// <param name="reportEnd">IThe end date/time for the report.</param>
        /// <returns></returns>
        [HttpGet("report")]
        [ProducesResponseType(typeof(List<VmUsageReport>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "GetVmUsageReport")]
        public async Task<IActionResult> GetVmUsageReport([FromQuery] DateTimeOffset reportStart, DateTimeOffset reportEnd)
        {
            if (_options.Enabled)
            {
                var result = await _mediator.Send(new GetVmUsageReport.Query {ReportStart = reportStart, ReportEnd = reportEnd});
                return Ok(result);
            }
            else
            {
                return NotFound("Vm Usage Logging is disabled");
            }
        }
    }
}
