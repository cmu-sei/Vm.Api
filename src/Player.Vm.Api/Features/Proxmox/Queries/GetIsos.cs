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
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Features.Vms;

namespace Player.Vm.Api.Features.Proxmox.Queries;

public class GetIsos
{
    [DataContract(Name = "GetProxmoxVirtualMachineIsos")]
    public class Query : IRequest<IsoResult[]>
    {
        [JsonIgnore]
        public Guid Id { get; set; }
    }

    public class Handler : BaseHandler, IRequestHandler<Query, IsoResult[]>
    {
        private readonly IIsoService _isoService;

        public Handler(
            VmContext db,
            IPlayerService playerService,
            IVmService vmService,
            IIsoService isoService)
            : base(db, playerService, vmService)
        {
            _isoService = isoService;
        }

        public async Task<IsoResult[]> Handle(Query request, CancellationToken cancellationToken)
        {
            // No extra permissions beyond being able to see the Vm: listing is what populates the mount
            // picker, and mounting itself is what requires edit rights.
            var vm = await GetVm(request.Id, [], [], [], cancellationToken);

            var viewTeams = await _isoService.ResolveViewTeamsForVmAsync(
                vm.VmTeams.Select(x => x.TeamId),
                cancellationToken);

            if (viewTeams.Count == 0)
                return Array.Empty<IsoResult>();

            // Vm-scoped rather than View-scoped: the MountValues come back straight to MountIso, so they
            // have to be volume ids from the storage this Vm's node can actually see.
            return await _isoService.BuildVmIsoResultsAsync(vm.Id, VmType.Proxmox, viewTeams, cancellationToken);
        }
    }
}
