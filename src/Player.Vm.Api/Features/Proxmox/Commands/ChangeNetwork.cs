// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Runtime.Serialization;
using System.Security.Principal;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Proxmox;

public class ChangeNetwork
{
    [DataContract(Name = "ChangeProxmoxVirtualMachineNetwork")]
    public class Command : IRequest<ProxmoxVirtualMachine>
    {
        [JsonIgnore]
        public Guid Id { get; set; }

        public string Adapter { get; set; }

        public string Network { get; set; }
    }

    public class Handler : NetworkBaseHandler, IRequestHandler<Command, ProxmoxVirtualMachine>
    {
        public Handler(
            IVmService vmService,
            IViewService viewService,
            INetworkService networkService,
            IProxmoxService proxmoxService,
            IPrincipal principal,
            ProxmoxOptions proxmoxOptions)
            : base(vmService, viewService, networkService, proxmoxService, principal, proxmoxOptions)
        {
        }

        public async Task<ProxmoxVirtualMachine> Handle(
            Command request,
            CancellationToken cancellationToken)
        {
            var (vm, viewId, permissions) = await GetVmAndPermissions(request.Id, cancellationToken);
            var allowedNetworks = permissions.AllowedNetworks ?? new();

            if (allowedNetworks.Count == 0)
                throw new ForbiddenException("You do not have permission to change networks on this VM");

            if (string.IsNullOrWhiteSpace(request.Adapter) || string.IsNullOrWhiteSpace(request.Network))
                throw new BadRequestException("An adapter and target network are required");

            if (!allowedNetworks.ContainsKey(request.Network))
                throw new ForbiddenException("The target network is not in your allowed networks list");

            await ProxmoxService.ChangeNetwork(
                ToDomainInfo(vm.ProxmoxVmInfo, vm.Id),
                request.Adapter,
                request.Network,
                cancellationToken);

            return await BuildVm(vm, viewId, permissions, cancellationToken);
        }
    }
}
