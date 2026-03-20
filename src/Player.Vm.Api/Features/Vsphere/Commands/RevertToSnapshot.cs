// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Features.Vsphere
{
    public class RevertToSnapshot
    {
        [DataContract(Name = "RevertToVsphereVirtualMachineSnapshot")]
        public class Command : IRequest, ICheckTasksRequest
        {
            [JsonIgnore]
            public Guid Id { get; set; }

            public string SnapshotId { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command>
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

            public async Task Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await base.GetVm(request.Id, [], [AppViewPermission.RevertVms], [], cancellationToken, "You do not have permission to revert this vm.");
                await _vsphereService.RevertToSnapshot(vm.Id, request.SnapshotId);
            }
        }
    }
}
