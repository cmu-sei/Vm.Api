// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Player.Vm.Api.Domain.Proxmox.Models;
using Swashbuckle.AspNetCore.Annotations;

namespace Player.Vm.Api.Features.Proxmox;

[Authorize]
[Route("api/")]
[ApiController]
public class ProxmoxController : Controller
{
    private readonly IMediator _mediator;

    public ProxmoxController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Retrieve the Url and Ticket for accessing a Proxmox virtual machine's NoVNC console
    /// </summary>
    [HttpGet("vms/proxmox/{id}/console")]
    [ProducesResponseType(typeof(ProxmoxConsole), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "getProxmoxConsole")]
    public async Task<IActionResult> Get([FromRoute] Guid id)
    {
        var result = await _mediator.Send(new GetConsole.Query { Id = id });
        return Json(result);
    }

}