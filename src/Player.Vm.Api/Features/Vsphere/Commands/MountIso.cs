// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Player.Vm.Api.Infrastructure.Exceptions;
using AutoMapper;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Domain.Vsphere.Extensions;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Vsphere
{
    public class MountIso
    {
        [DataContract(Name = "MountVsphereIso")]
        public class Command : IRequest<VsphereVirtualMachine>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string Iso { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, VsphereVirtualMachine>
        {
            private readonly IVsphereService _vsphereService;
            private readonly IVmService _vmService;
            private readonly IMapper _mapper;

            public Handler(
                IVsphereService vsphereService,
                IVmService vmService,
                IMapper mapper,
                IPlayerService playerService,
                IPrincipal principal,
                IPermissionsService permissionsService) :
                base(mapper, vsphereService, playerService, principal, permissionsService, vmService)
            {
                _vsphereService = vsphereService;
                _vmService = vmService;
                _mapper = mapper;
            }

            public async Task<VsphereVirtualMachine> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await base.GetVm(request.Id, Permissions.ReadOnly, cancellationToken);
                await _vsphereService.ReconfigureVm(request.Id, Feature.iso, "", request.Iso);

                return await base.GetVsphereVirtualMachine(vm, cancellationToken);
            }
        }
    }
}