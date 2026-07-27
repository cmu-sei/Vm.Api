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
using Player.Vm.Api.Domain.Services;
using System.Linq;
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
            private readonly IPlayerService _playerService;
            private readonly IViewService _viewService;
            private readonly IIsoService _isoService;

            public Handler(
                IVmService vmService,
                IPlayerService playerService,
                IViewService viewService,
                IIsoService isoService)
            {
                _vmService = vmService;
                _playerService = playerService;
                _viewService = viewService;
                _isoService = isoService;
            }

            public async Task<IsoResult[]> Handle(Query request, CancellationToken cancellationToken)
            {
                var vm = await _vmService.GetAsync(request.Id, cancellationToken);

                if (vm == null)
                    throw new EntityNotFoundException<VsphereVirtualMachine>();

                var viewIds = await _viewService.GetViewIdsForTeams(vm.TeamIds, cancellationToken);

                var viewTeamsTasks = viewIds.Select(async viewId =>
                {
                    var teams = (await _playerService.GetTeamsByViewIdAsync(viewId, cancellationToken)).ToList();

                    // No teams => caller has no access to this View; skip it.
                    if (teams.Count == 0)
                        return (ViewTeams)null;

                    var view = await _playerService.GetViewByIdAsync(viewId, cancellationToken);
                    return new ViewTeams(view, teams);
                });

                var viewTeams = (await Task.WhenAll(viewTeamsTasks))
                    .Where(vt => vt != null)
                    .ToList();

                if (viewTeams.Count == 0)
                    return Array.Empty<IsoResult>();

                // VM-scoped listing: the returned paths are handed back to MountIso, so they must come
                // from the host this VM runs on rather than any connected host.
                return await _isoService.BuildVmIsoResultsAsync(vm.Id, viewTeams, cancellationToken);
            }
        }
    }
}
