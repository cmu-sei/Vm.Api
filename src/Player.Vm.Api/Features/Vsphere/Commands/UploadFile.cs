// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using System.IO;
using Microsoft.AspNetCore.Http;
using AutoMapper;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Features.Vsphere
{
    public class UploadFile
    {
        [DataContract(Name = "UploadFileToVsphereVirtualMachine")]
        public class Command : IRequest<string>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string FilePath { get; set; }
            public IFormFileCollection Files { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, string>
        {
            private readonly IVsphereService _vsphereService;

            public Handler(
                IVsphereService vsphereService,
                IVmService vmService,
                IMapper mapper,
                IPlayerService playerService,
                IPrincipal principal) :
                base(mapper, vsphereService, playerService, principal, vmService)
            {
                _vsphereService = vsphereService;
            }

            public async Task<string> Handle(Command request, CancellationToken cancellationToken)
            {
                await base.GetVm(request.Id, [], [AppViewPermission.UploadVmFiles], [], cancellationToken, "You do not have permission to upload files to this vm.");

                foreach (var formFile in request.Files)
                {
                    using (Stream fileStream = formFile.OpenReadStream())
                    {
                        try
                        {
                            await _vsphereService.UploadFileToVm(
                                request.Id,
                                request.Username,
                                request.Password,
                                string.Format("{0}{1}", request.FilePath, formFile.FileName),
                                fileStream);
                        }
                        catch (Exception ex)
                        {
                            throw new BadRequestException(ex.Message);
                        }
                    }
                }

                return "Files were successfully uploaded.";
            }
        }
    }
}