// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using AutoMapper;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Features.Vsphere
{
    public class ReadGuestFile
    {
        [DataContract(Name = "ReadGuestFileFromVsphereVirtualMachine")]
        public class Command : IRequest<string>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string GuestFilePath { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, string>
        {
            private readonly IVsphereService _vsphereService;

            public Handler(
                IVsphereService vsphereService,
                IVmService vmService,
                IMapper mapper,
                IPlayerService playerService,
                IPrincipal principal,
                IViewService viewService) :
                base(mapper, vsphereService, playerService, principal, vmService, viewService)
            {
                _vsphereService = vsphereService;
            }

            public async Task<string> Handle(Command request, CancellationToken cancellationToken)
            {
                await base.GetVm(request.Id, [], [AppViewPermission.DownloadVmFiles], [], cancellationToken, "You do not have permission to download files from this vm.");

                return await _vsphereService.ReadGuestFileAsync(
                    request.Id,
                    request.Username,
                    request.Password,
                    request.GuestFilePath);
            }
        }
    }
}
