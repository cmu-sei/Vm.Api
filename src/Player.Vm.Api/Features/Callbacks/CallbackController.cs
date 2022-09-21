// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Swashbuckle.AspNetCore.Annotations;
using Player.Api.Client;
using Microsoft.AspNetCore.Authorization;
using Player.Vm.Api.Infrastructure.Constants;
using MediatR;

namespace Player.Vm.Api.Features.Callbacks
{
    [Authorize(Constants.PrivilegedAuthorizationPolicy)]
    public class CallbacksController : ControllerBase
    {
        private readonly IMediator _mediator;

        public CallbacksController(IMediator mediator)
        {
            _mediator = mediator;
        }

        /// <summary>
        /// Receive a webhook event
        /// </summary>
        /// <param name="evt"></param>
        /// <param name="ct"></param>
        [HttpPost("api/callback")]
        [ProducesResponseType((int)HttpStatusCode.Accepted)]
        [SwaggerOperation(OperationId = "handleCallback")]
        public async Task<IActionResult> Handle([FromBody] WebhookEvent evt, CancellationToken ct)
        {
            if (await _mediator.Send(new HandleCallback.Command { CallbackEvent = evt }))
            {
                return Accepted();
            }
            else
            {
                return Problem(statusCode: (int)HttpStatusCode.InternalServerError);
            }
        }
    }
}