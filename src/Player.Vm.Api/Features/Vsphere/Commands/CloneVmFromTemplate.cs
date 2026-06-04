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
    public class CloneVmFromTemplate
    {
        [DataContract(Name = "CloneVsphereVirtualMachineFromTemplate")]
        public class Command : IRequest<Response>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string CloneName { get; set; }
            public bool PowerOn { get; set; }
        }

        [DataContract(Name = "CloneVsphereVirtualMachineFromTemplateResponse")]
        public class Response
        {
            public Guid Id { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, Response>
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

            public async Task<Response> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await base.GetVmForEditing(request.Id, cancellationToken);
                var cloneId = await _vsphereService.CloneVmFromTemplateAsync(vm.Id, request.CloneName, request.PowerOn);
                return new Response { Id = cloneId };
            }
        }
    }
}
