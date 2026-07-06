// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Proxmox.Commands
{
    public class UploadFile
    {
        [DataContract(Name = "UploadFileToProxmoxVirtualMachine")]
        public class Command : IRequest<string>
        {
            [FromRoute(Name = "id")]
            public Guid Id { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string FilePath { get; set; }
            public IFormFileCollection Files { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, string>
        {
            private readonly IProxmoxService _proxmoxService;

            public Handler(VmContext db, IPlayerService playerService, IProxmoxService proxmoxService)
                : base(db, playerService)
            {
                _proxmoxService = proxmoxService;
            }

            public async Task<string> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await GetVm(request.Id, [], [AppViewPermission.UploadVmFiles], [], cancellationToken,
                    "You do not have permission to upload files to this vm.");

                foreach (var formFile in request.Files)
                {
                    using Stream fileStream = formFile.OpenReadStream();
                    try
                    {
                        await _proxmoxService.UploadFileToGuest(
                            vm.ProxmoxVmInfo,
                            $"{request.FilePath}{formFile.FileName}",
                            fileStream);
                    }
                    catch (Exception ex)
                    {
                        throw new BadRequestException(ex.Message);
                    }
                }

                return "Files were successfully uploaded.";
            }
        }
    }
}
