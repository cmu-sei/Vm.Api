// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Proxmox.Commands;

public class MountIso
{
    [DataContract(Name = "MountProxmoxVirtualMachineIso")]
    public class Command : IRequest<ProxmoxVirtualMachine>
    {
        [JsonIgnore]
        public Guid Id { get; set; }

        /// <summary>
        /// The Proxmox volume id of the ISO to mount, as returned in the MountValue of
        /// GET vms/proxmox/{id}/isos. Only volumes on the configured ISO storage whose name encodes a
        /// View and team of this Vm are accepted.
        /// </summary>
        public string Iso { get; set; }
    }

    public class Handler : BaseHandler, IRequestHandler<Command, ProxmoxVirtualMachine>
    {
        private readonly IProxmoxVmNetworkService _networkService;
        private readonly IProxmoxService _proxmoxService;
        private readonly IIsoService _isoService;
        private readonly IXApiService _xApiService;

        public Handler(
            VmContext db,
            IPlayerService playerService,
            IVmService vmService,
            IProxmoxVmNetworkService networkService,
            IProxmoxService proxmoxService,
            IIsoService isoService,
            IXApiService xApiService)
            : base(db, playerService, vmService)
        {
            _networkService = networkService;
            _proxmoxService = proxmoxService;
            _isoService = isoService;
            _xApiService = xApiService;
        }

        public async Task<ProxmoxVirtualMachine> Handle(Command request, CancellationToken cancellationToken)
        {
            var vm = await GetVmForEditing(request.Id, cancellationToken);

            if (string.IsNullOrWhiteSpace(request.Iso))
                throw new BadRequestException("An iso is required");

            // The volume id goes straight into the Vm's cdrom drive definition, and a Proxmox volid can
            // name any volume the cluster has - including another Vm's disk image, which would then be
            // readable as a CD. So it is never taken on trust: it has to decode to an ISO scope that
            // belongs to this Vm, with the caller holding edit rights over that scope, and what gets
            // mounted is the volid rebuilt from that scope rather than the one submitted.
            var iso = await _isoService.ResolveMountValueAsync(
                vm.Id, VmType.Proxmox, vm.VmTeams.Select(x => x.TeamId), request.Iso, cancellationToken);

            await _proxmoxService.MountIso(vm.ProxmoxVmInfo, iso, cancellationToken);
            await _xApiService.TrackIsoMountedAsync(vm.Id, iso, cancellationToken);

            var permissions = await _networkService.GetPermissions(vm, cancellationToken);
            return await _networkService.ToResponse(vm, permissions, cancellationToken);
        }
    }
}
