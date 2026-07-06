// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Authorization;
using System.Security.Principal;

namespace Player.Vm.Api.Features.Vsphere
{
    public class CreateSnapshot
    {
        [DataContract(Name = "CreateVsphereVirtualMachineSnapshot")]
        public class Command : IRequest<string>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string SnapshotName { get; set; }
            public string Description { get; set; }
            public bool IncludeMemory { get; set; }
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
                var vm = await base.GetVm(request.Id, [], [AppViewPermission.RevertVms], [], cancellationToken,
                    "You do not have permission to manage snapshots for this vm.");
                return await _vsphereService.CreateSnapshot(
                    vm.Id, request.SnapshotName, request.Description, request.IncludeMemory);
            }
        }
    }
}
