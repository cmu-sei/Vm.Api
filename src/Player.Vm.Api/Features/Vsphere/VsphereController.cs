// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VimClient;
using Swashbuckle.AspNetCore.Annotations;

namespace Player.Vm.Api.Features.Vsphere
{
    [Authorize]
    [Route("api/")]
    [ApiController]
    public class VsphereController : Controller
    {
        private readonly IMediator _mediator;

        public VsphereController(IMediator mediator)
        {
            _mediator = mediator;
        }

        /// <summary>
        /// Retrieve a single vsphere virtual machine by Id, including a ticket to access it's console
        /// </summary>
        [HttpGet("vms/vsphere/{id}")]
        [ProducesResponseType(typeof(VsphereVirtualMachine), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getVsphereVirtualMachine")]
        public async Task<IActionResult> Get([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new Get.Query { Id = id });
            return Json(result);
        }

        /// <summary>
        /// Power on a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/power-on")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "powerOnVsphereVirtualMachine")]
        public async Task<IActionResult> PowerOn([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new PowerOn.Command { Id = id });
            return Json(result);
        }

        /// <summary>
        /// Power off a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/power-off")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "powerOffVsphereVirtualMachine")]
        public async Task<IActionResult> PowerOff([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new PowerOff.Command { Id = id });
            return Json(result);
        }

        /// <summary>
        /// Reboot a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/reboot")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "rebootVsphereVirtualMachine")]
        public async Task<IActionResult> Reboot([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new Reboot.Command { Id = id });
            return Json(result);
        }

        /// <summary>
        /// Shutdown a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/shutdown")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "shutdownVsphereVirtualMachine")]
        public async Task<IActionResult> Shutdown([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new Shutdown.Command { Id = id });
            return Json(result);
        }

        /// <summary>
        /// Revert to the current snapshot of a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/revert")]
        [ProducesResponseType((int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "revertVsphereVirtualMachine")]
        public async Task<IActionResult> Revert([FromRoute] Guid id)
        {
            await _mediator.Send(new Revert.Command { Id = id });
            return Ok();
        }

        /// <summary>
        /// Get tools status of a vsphere virtual machine
        /// </summary>
        [HttpGet("vms/vsphere/{id}/tools")]
        [ProducesResponseType(typeof(VirtualMachineToolsStatus), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getVsphereVirtualMachineToolsStatus")]
        public async Task<IActionResult> GetToolsStatus([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new GetToolsStatus.Query { Id = id });
            return Json(result);
        }

        /// <summary>
        /// Change the network of a vsphere virtual machine's network adapter
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/change-network")]
        [ProducesResponseType(typeof(VsphereVirtualMachine), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "changeVsphereVirtualMachineNetwork")]
        public async Task<IActionResult> ChangeNetwork([FromRoute] Guid id, [FromBody] ChangeNetwork.Command command)
        {
            command.Id = id;
            var result = await _mediator.Send(command);
            return Json(result);
        }

        /// <summary>
        /// Validate credentials for a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/validate-credentials")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "validateVsphereVirtualMachineCredentials")]
        public async Task<IActionResult> ValidateCredentials([FromRoute] Guid id, [FromBody] ValidateCredentials.Command command)
        {
            command.Id = id;
            var result = await _mediator.Send(command);
            return Json(result);
        }

        /// <summary>
        /// Upload a file to a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/upload-file"), DisableRequestSizeLimit]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "uploadFileToVsphereVirtualMachine")]
        public async Task<IActionResult> UploadFile([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new UploadFile.Command
            {
                Id = id,
                Files = Request.Form.Files,
                FilePath = Request.Form["filepath"][0],
                Password = Request.Form["password"][0],
                Username = Request.Form["username"][0]
            });
            return Json(result);
        }

        /// <summary>
        /// Get the url to download a file from a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/file-url")]
        [ProducesResponseType(typeof(GetVmFileUrl.Response), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getFileUrlVsphereVirtualMachine")]
        public async Task<IActionResult> GetFileUrl([FromRoute] Guid id, [FromBody] GetVmFileUrl.Command command)
        {
            command.Id = id;
            return Ok(await _mediator.Send(command));
        }

        /// <summary>
        /// Get isos available to be mounted to a vsphere virtual machine
        /// </summary>
        [HttpGet("vms/vsphere/{id}/isos")]
        [ProducesResponseType(typeof(GetIsos.IsoResult[]), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getVsphereVirtualMachineIsos")]
        public async Task<IActionResult> GetIsos([FromRoute] Guid id)
        {
            var result = await _mediator.Send(new GetIsos.Query { Id = id });
            return Json(result);
        }

        /// <summary>
        /// Mount an iso to a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/mount-iso")]
        [ProducesResponseType(typeof(VsphereVirtualMachine), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "mountVsphereVirtualMachineIso")]
        public async Task<IActionResult> MountIso([FromRoute] Guid id, [FromBody] MountIso.Command command)
        {
            command.Id = id;
            var result = await _mediator.Send(command);
            return Json(result);
        }

        /// <summary>
        /// Set the resolution of a vsphere virtual machine
        /// </summary>
        [HttpPost("vms/vsphere/{id}/actions/set-resolution")]
        [ProducesResponseType(typeof(string), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "setVsphereVirtualMachineResolution")]
        public async Task<IActionResult> SetResolution([FromRoute] Guid id, [FromBody] SetResolution.Command command)
        {
            command.Id = id;
            var result = await _mediator.Send(command);
            return Json(result);
        }
    }
}