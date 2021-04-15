// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Swashbuckle.AspNetCore.Annotations;
using Player.Vm.Api.Domain.Vsphere.Models;
using MediatR;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Vms;
using Player.Api.Client;
using Player.Vm.Api.Infrastructure.BackgroundServices;
using Newtonsoft.Json;

namespace Player.Vm.Api.Features.Callbacks
{
    public class CallbacksController : ControllerBase
    {
        private readonly IVmService _vmService;
        private readonly ICallbackBackgroundService _backgroundService;

        public CallbacksController(IVmService vmService, ICallbackBackgroundService backgroundService)
        {
            _vmService = vmService;
            _backgroundService = backgroundService;
        }

        /// <summary>
        /// Receive a webhook event
        /// </summary>
        /// <param name="ct"></param>
        [HttpPost("api/callback")]
        [ProducesResponseType((int) HttpStatusCode.Accepted)]
        [SwaggerOperation(OperationId = "respond")]
        public async Task<IActionResult> Respond([FromBody] WebhookEvent evt, CancellationToken ct)
        {
            _backgroundService.AddEvent(evt);
            return Accepted();
        }
    }
}