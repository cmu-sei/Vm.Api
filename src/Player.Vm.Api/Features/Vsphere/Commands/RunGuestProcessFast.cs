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

namespace Player.Vm.Api.Features.Vsphere
{
    public class RunGuestProcessFast
    {
        [DataContract(Name = "RunGuestProcessFastOnVsphereVirtualMachine")]
        public class Command : IRequest<long>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string ProgramPath { get; set; }
            public string Arguments { get; set; }
            public string WorkingDirectory { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, long>
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

            public async Task<long> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await base.GetVmForEditing(request.Id, cancellationToken);

                return await _vsphereService.RunGuestProcessFastAsync(
                    vm.Id,
                    request.Username,
                    request.Password,
                    request.ProgramPath,
                    request.Arguments,
                    request.WorkingDirectory);
            }
        }
    }
}
