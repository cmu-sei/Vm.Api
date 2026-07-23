// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Player.Vm.Api.Domain.Proxmox.Models;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Features.Proxmox.Commands;
using Player.Vm.Api.Features.Proxmox.Queries;
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

    /// <summary>
    /// Power on a proxmox virtual machine
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/power-on")]
    [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "powerOnProxmoxVirtualMachine")]
    public async Task<IActionResult> PowerOn([FromRoute] Guid id)
    {
        var result = await _mediator.Send(new PowerOn.Command { Id = id });
        return Json(result);
    }

    /// <summary>
    /// Power off a proxmox virtual machine
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/power-off")]
    [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "powerOffProxmoxVirtualMachine")]
    public async Task<IActionResult> PowerOff([FromRoute] Guid id)
    {
        var result = await _mediator.Send(new PowerOff.Command { Id = id });
        return Json(result);
    }

    /// <summary>
    /// Reboot a proxmox virtual machine
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/reboot")]
    [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "rebootProxmoxVirtualMachine")]
    public async Task<IActionResult> Reboot([FromRoute] Guid id)
    {
        var result = await _mediator.Send(new Reboot.Command { Id = id });
        return Json(result);
    }

    /// <summary>
    /// Shutdown a proxmox virtual machine
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/shutdown")]
    [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "shutdownProxmoxVirtualMachine")]
    public async Task<IActionResult> Shutdown([FromRoute] Guid id)
    {
        var result = await _mediator.Send(new Shutdown.Command { Id = id });
        return Json(result);
    }

    /// <summary>
    /// Run a process inside the guest OS of a proxmox virtual machine via qemu-guest-agent and wait for completion
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/run-guest-process")]
    [ProducesResponseType(typeof(GuestProcessResult), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "runGuestProcessOnProxmoxVirtualMachine")]
    public async Task<IActionResult> RunGuestProcess([FromRoute] Guid id, [FromBody] RunGuestProcess.Command command)
    {
        command.Id = id;
        var result = await _mediator.Send(command);
        return Json(result);
    }

    /// <summary>
    /// Start a process inside the guest OS of a proxmox virtual machine via qemu-guest-agent without waiting
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/run-guest-process-fast")]
    [ProducesResponseType(typeof(long), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "runGuestProcessFastOnProxmoxVirtualMachine")]
    public async Task<IActionResult> RunGuestProcessFast([FromRoute] Guid id, [FromBody] RunGuestProcessFast.Command command)
    {
        command.Id = id;
        var result = await _mediator.Send(command);
        return Json(result);
    }

    /// <summary>
    /// Read the contents of a file from the guest OS of a proxmox virtual machine via qemu-guest-agent
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/read-guest-file")]
    [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "readGuestFileFromProxmoxVirtualMachine")]
    public async Task<IActionResult> ReadGuestFile([FromRoute] Guid id, [FromBody] ReadGuestFile.Command command)
    {
        command.Id = id;
        var result = await _mediator.Send(command);
        return Json(result);
    }

    /// <summary>
    /// Upload a file to a proxmox virtual machine via qemu-guest-agent (limited to ~60 KiB per file)
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/upload-file"), DisableRequestSizeLimit]
    [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "uploadFileToProxmoxVirtualMachine")]
    public async Task<IActionResult> UploadFile([FromForm] UploadFile.Command command)
    {
        command.Files = Request.Form.Files;
        var result = await _mediator.Send(command);
        return Json(result);
    }

    /// <summary>
    /// Get all snapshots for a proxmox virtual machine
    /// </summary>
    [HttpGet("vms/proxmox/{id}/snapshots")]
    [ProducesResponseType(typeof(List<ProxmoxSnapshot>), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "getProxmoxVirtualMachineSnapshots")]
    public async Task<IActionResult> GetSnapshots([FromRoute] Guid id)
    {
        var result = await _mediator.Send(new GetSnapshots.Query { Id = id });
        return Json(result);
    }

    /// <summary>
    /// Create a snapshot of a proxmox virtual machine
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/snapshots")]
    [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "createProxmoxVirtualMachineSnapshot")]
    public async Task<IActionResult> CreateSnapshot([FromRoute] Guid id, [FromBody] CreateSnapshot.Command command)
    {
        command.Id = id;
        var result = await _mediator.Send(command);
        return Json(result);
    }

    /// <summary>
    /// Revert a proxmox virtual machine to a specific snapshot
    /// </summary>
    [HttpPost("vms/proxmox/{id}/actions/snapshots/{name}/revert")]
    [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "revertProxmoxVirtualMachineSnapshot")]
    public async Task<IActionResult> RevertSnapshot([FromRoute] Guid id, [FromRoute] string name)
    {
        var result = await _mediator.Send(new RevertSnapshot.Command { Id = id, SnapshotName = name });
        return Json(result);
    }

    /// <summary>
    /// Delete a snapshot of a proxmox virtual machine
    /// </summary>
    [HttpDelete("vms/proxmox/{id}/actions/snapshots/{name}")]
    [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
    [SwaggerOperation(OperationId = "deleteProxmoxVirtualMachineSnapshot")]
    public async Task<IActionResult> DeleteSnapshot([FromRoute] Guid id, [FromRoute] string name)
    {
        var result = await _mediator.Send(new DeleteSnapshot.Command { Id = id, SnapshotName = name });
        return Json(result);
    }
}
