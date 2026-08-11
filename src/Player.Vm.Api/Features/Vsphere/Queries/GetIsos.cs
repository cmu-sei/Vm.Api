// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files;

namespace Player.Vm.Api.Features.Vsphere
{
    public class GetIsos
    {
        [DataContract(Name = "GetVsphereVirtualMachineIsos")]
        public class Query : IRequest<IsoResult[]>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
        }

        public class Handler : IRequestHandler<Query, IsoResult[]>
        {
            private readonly IVmService _vmService;
            private readonly IIsoService _isoService;

            public Handler(
                IVmService vmService,
                IIsoService isoService)
            {
                _vmService = vmService;
                _isoService = isoService;
            }

            public async Task<IsoResult[]> Handle(Query request, CancellationToken cancellationToken)
            {
                var vm = await _vmService.GetAsync(request.Id, cancellationToken);

                if (vm == null)
                    throw new EntityNotFoundException<VsphereVirtualMachine>();

                var viewTeams = await _isoService.ResolveViewTeamsForVmAsync(vm.TeamIds, cancellationToken);

                if (viewTeams.Count == 0)
                    return Array.Empty<IsoResult>();

                // VM-scoped listing: the returned paths are handed back to MountIso, so they must come
                // from the host this VM runs on rather than any connected host.
                return await _isoService.BuildVmIsoResultsAsync(vm.Id, VmType.Vsphere, viewTeams, cancellationToken);
            }
        }
    }
}
