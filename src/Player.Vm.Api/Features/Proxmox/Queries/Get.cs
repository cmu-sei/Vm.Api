// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
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

    public class Handler : NetworkBaseHandler, IRequestHandler<Query, ProxmoxVirtualMachine>
    {
        public Handler(
            IVmService vmService,
            IViewService viewService,
            INetworkService networkService,
            IProxmoxService proxmoxService,
            ProxmoxOptions proxmoxOptions)
            : base(vmService, viewService, networkService, proxmoxService, proxmoxOptions)
        {
        }

        public async Task<ProxmoxVirtualMachine> Handle(
            Query request,
            CancellationToken cancellationToken)
        {
            var (vm, viewId, permissions) = await GetVmAndPermissions(request.Id, cancellationToken);
            return await BuildVm(vm, viewId, permissions, cancellationToken);
        }
    }
}
