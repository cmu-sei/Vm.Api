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
        /// GET vms/proxmox/{id}/isos. Only values from that listing are accepted.
        /// </summary>
        public string Iso { get; set; }
    }

    public class Handler : BaseHandler, IRequestHandler<Command, ProxmoxVirtualMachine>
    {
        private readonly IProxmoxVmNetworkService _networkService;
        private readonly IProxmoxService _proxmoxService;
        private readonly IIsoService _isoService;

        public Handler(
            VmContext db,
            IPlayerService playerService,
            IVmService vmService,
            IProxmoxVmNetworkService networkService,
            IProxmoxService proxmoxService,
            IIsoService isoService)
            : base(db, playerService, vmService)
        {
            _networkService = networkService;
            _proxmoxService = proxmoxService;
            _isoService = isoService;
        }

        public async Task<ProxmoxVirtualMachine> Handle(Command request, CancellationToken cancellationToken)
        {
            var vm = await GetVmForEditing(request.Id, cancellationToken);

            if (string.IsNullOrWhiteSpace(request.Iso))
                throw new BadRequestException("An iso is required");

            // The volume id goes straight into the Vm's cdrom drive definition, and a Proxmox volid can
            // name any volume on the storage - including another Vm's disk image, which would then be
            // readable as a CD. So it is never taken on trust: it has to be one of the volume ids THIS
            // caller's own listing for THIS Vm just returned, which is already scoped to the Views and
            // teams they can see.
            var viewTeams = await _isoService.ResolveViewTeamsForVmAsync(
                vm.VmTeams.Select(x => x.TeamId), cancellationToken);

            var allowed = await _isoService.BuildVmIsoResultsAsync(
                vm.Id, VmType.Proxmox, viewTeams, cancellationToken);

            var isAllowed = allowed
                .SelectMany(result => result.Isos.Concat(result.TeamIsoResults.SelectMany(t => t.Isos)))
                .Any(iso => string.Equals(iso.MountValue, request.Iso, StringComparison.Ordinal));

            if (!isAllowed)
                throw new ForbiddenException("The specified iso is not available to this Vm");

            await _proxmoxService.MountIso(vm.ProxmoxVmInfo, request.Iso, cancellationToken);

            var permissions = await _networkService.GetPermissions(vm, cancellationToken);
            return await _networkService.ToResponse(vm, permissions, cancellationToken);
        }
    }
}
