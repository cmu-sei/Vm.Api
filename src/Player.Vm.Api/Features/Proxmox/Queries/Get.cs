// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;

namespace Player.Vm.Api.Features.Proxmox.Queries;

public class Get
{
    [DataContract(Name = "GetProxmoxVirtualMachineQuery")]
    public class Query : IRequest<ProxmoxVirtualMachine>
    {
        public Guid Id { get; set; }
    }

    public class Handler : BaseHandler, IRequestHandler<Query, ProxmoxVirtualMachine>
    {
        private readonly IProxmoxVmNetworkService _networkService;

        public Handler(
            VmContext db,
            IPlayerService playerService,
            IVmService vmService,
            IProxmoxVmNetworkService networkService)
            : base(db, playerService, vmService)
        {
            _networkService = networkService;
        }

        public async Task<ProxmoxVirtualMachine> Handle(
            Query request,
            CancellationToken cancellationToken)
        {
            // No permission arrays: read access is settled by GetVm, and which networks are
            // selectable is settled by the view-network permissions below.
            var vm = await GetVm(request.Id, [], [], [], cancellationToken);
            var permissions = await _networkService.GetPermissions(vm, cancellationToken);

            return await _networkService.ToResponse(vm, permissions, cancellationToken);
        }
    }
}
