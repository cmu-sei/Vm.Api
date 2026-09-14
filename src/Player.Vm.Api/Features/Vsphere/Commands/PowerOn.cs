// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Vsphere
{
    public class PowerOn
    {
        [DataContract(Name = "PowerOnVsphereVirtualMachine")]
        public class Command : IRequest<string>, ICheckVsphereTasksRequest
        {
            [JsonIgnore]
            public Guid Id { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, string>
        {
            private readonly IVsphereService _vsphereService;
            private readonly IXApiService _xApiService;

            public Handler(
                IVsphereService vsphereService,
                IVmService vmService,
                IMapper mapper,
                IPlayerService playerService,
                IPrincipal principal,
                IViewService viewService,
                IXApiService xApiService) :
                base(mapper, vsphereService, playerService, principal, vmService, viewService)
            {
                _vsphereService = vsphereService;
                _xApiService = xApiService;
            }

            public async Task<string> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await base.GetVmForEditing(request.Id, cancellationToken);

                var result = await _vsphereService.PowerOnVm(vm.Id);
                await _xApiService.TrackPowerOperationAsync(vm.Id, PowerOperation.PowerOn, cancellationToken);
                return result;
            }
        }
    }
}
