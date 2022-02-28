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
using VimClient;

namespace Player.Vm.Api.Features.Vsphere
{
    public class GetToolsStatus
    {
        [DataContract(Name = "GetVsphereVirtualMachineToolsStatus")]
        public class Query : IRequest<VirtualMachineToolsStatus>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
        }

        public class Handler : IRequestHandler<Query, VirtualMachineToolsStatus>
        {
            private readonly IVsphereService _vsphereService;
            private readonly IVmService _vmService;

            public Handler(
                IVsphereService vsphereService,
                IVmService vmService)
            {
                _vsphereService = vsphereService;
                _vmService = vmService;
            }

            public async Task<VirtualMachineToolsStatus> Handle(Query request, CancellationToken cancellationToken)
            {
                var vm = await _vmService.GetAsync(request.Id, cancellationToken);

                if (vm == null)
                    throw new EntityNotFoundException<VsphereVirtualMachine>();

                return await _vsphereService.GetVmToolsStatus(request.Id);
            }
        }
    }
}