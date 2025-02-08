// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
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
using AutoMapper;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Features.Vsphere
{
    public class GetVmFileUrl
    {
        [DataContract(Name = "GetFileUrlVsphereVirtualMachine")]
        public class Command : IRequest<Response>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string FilePath { get; set; }
        }

        [DataContract(Name = "FileVmUrlResponse")]
        public class Response
        {
            public string Url { get; set; }
            public string FileName { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, Response>
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

            public async Task<Response> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await base.GetVm(request.Id, [], [AppViewPermission.DownloadVmFiles], [], cancellationToken, "You do not have permission to download files from this vm.");

                var url = await _vsphereService.GetVmFileUrl(
                    request.Id,
                    request.Username,
                    request.Password,
                    request.FilePath);

                var fileName = Path.GetFileName(request.FilePath);

                return new Response()
                {
                    FileName = fileName,
                    Url = url
                };
            }
        }
    }
}