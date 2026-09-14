// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Proxmox.Commands;

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

    public class Handler : BaseHandler, IRequestHandler<Command, ProxmoxVirtualMachine>
    {
        private readonly IProxmoxVmNetworkService _networkService;
        private readonly IProxmoxService _proxmoxService;
        private readonly IXApiService _xApiService;

        public Handler(
            VmContext db,
            IPlayerService playerService,
            IVmService vmService,
            IProxmoxVmNetworkService networkService,
            IProxmoxService proxmoxService,
            IXApiService xApiService)
            : base(db, playerService, vmService)
        {
            _networkService = networkService;
            _proxmoxService = proxmoxService;
            _xApiService = xApiService;
        }

        public async Task<ProxmoxVirtualMachine> Handle(
            Command request,
            CancellationToken cancellationToken)
        {
            var vm = await GetVm(request.Id, [], [], [], cancellationToken);
            var permissions = await _networkService.GetPermissions(vm, cancellationToken);
            var allowedNetworks = permissions.Permissions.AllowedNetworks ?? new();

            if (allowedNetworks.Count == 0)
                throw new ForbiddenException("You do not have permission to change networks on this VM");

            if (string.IsNullOrWhiteSpace(request.Adapter) || string.IsNullOrWhiteSpace(request.Network))
                throw new BadRequestException("An adapter and target network are required");

            if (!allowedNetworks.ContainsKey(request.Network))
                throw new ForbiddenException("The target network is not in your allowed networks list");

            await _proxmoxService.ChangeNetwork(
                vm.ProxmoxVmInfo,
                request.Adapter,
                request.Network,
                cancellationToken);
            await _xApiService.TrackNetworkChangedAsync(
                vm.Id,
                request.Adapter,
                request.Network,
                cancellationToken);

            return await _networkService.ToResponse(vm, permissions, cancellationToken);
        }
    }
}
