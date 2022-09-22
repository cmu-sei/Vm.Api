// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using System.Collections.Generic;
using Player.Vm.Api.Domain.Services;
using System.Linq;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Api.Client;

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

        public class IsoResult
        {
            public Guid ViewId { get; set; }
            public string ViewName { get; set; }
            public IsoFile[] Isos { get; set; }
            public List<TeamIsoResult> TeamIsoResults { get; set; } = new List<TeamIsoResult>();
        }

        public class TeamIsoResult
        {
            public Guid TeamId { get; set; }
            public string TeamName { get; set; }
            public IsoFile[] Isos { get; set; }
        }

        public class Handler : IRequestHandler<Query, IsoResult[]>
        {
            private readonly IVmService _vmService;
            private readonly IVsphereService _vsphereService;
            private readonly IPlayerService _playerService;
            private readonly IViewService _viewService;

            public Handler(
                IVmService vmService,
                IVsphereService vsphereService,
                IPlayerService playerService,
                IViewService viewService)
            {
                _vmService = vmService;
                _vsphereService = vsphereService;
                _playerService = playerService;
                _viewService = viewService;
            }

            public async Task<IsoResult[]> Handle(Query request, CancellationToken cancellationToken)
            {
                var vm = await _vmService.GetAsync(request.Id, cancellationToken);

                if (vm == null)
                    throw new EntityNotFoundException<VsphereVirtualMachine>();

                var results = new List<IsoResult>();
                var viewIds = await _viewService.GetViewIdsForTeams(vm.TeamIds, cancellationToken);

                var isoTasks = new List<Task<IsoResult>>();

                foreach (var viewId in viewIds)
                {
                    isoTasks.Add(this.GetViewIsos(viewId, cancellationToken));
                }

                await Task.WhenAll(isoTasks);

                foreach (var isoResult in isoTasks.Select(x => x.Result))
                {
                    if (isoResult != null)
                    {
                        results.Add(isoResult);
                    }
                }

                return results.ToArray();
            }

            private async Task<IsoResult> GetViewIsos(Guid viewId, CancellationToken cancellationToken)
            {
                var teams = await _playerService.GetTeamsByViewIdAsync(viewId, cancellationToken);

                // User has access to this view
                if (teams.Count() > 0)
                {
                    return await this.GetViewIsos(viewId, teams, cancellationToken);
                }
                else
                {
                    return null;
                }
            }

            private async Task<IsoResult> GetViewIsos(Guid viewId, IEnumerable<Team> teams, CancellationToken cancellationToken)
            {
                var viewTask = _playerService.GetViewByIdAsync(viewId, cancellationToken);

                var isoTaskDict = new Dictionary<Guid, Task<IEnumerable<IsoFile>>>();
                isoTaskDict.Add(viewId, _vsphereService.GetIsos(viewId.ToString(), viewId.ToString()));

                foreach (var team in teams)
                {
                    isoTaskDict.Add(team.Id, _vsphereService.GetIsos(viewId.ToString(), team.Id.ToString()));
                }

                var tasks = new List<Task>();
                tasks.Add(viewTask);
                tasks.AddRange(isoTaskDict.Values);

                await Task.WhenAll(tasks);

                var view = viewTask.Result;

                var isoResult = new IsoResult
                {
                    ViewId = view.Id,
                    ViewName = view.Name
                };

                foreach (var kvp in isoTaskDict)
                {
                    if (kvp.Key == viewId)
                    {
                        isoResult.Isos = kvp.Value.Result.ToArray();
                    }
                    else
                    {
                        isoResult.TeamIsoResults.Add(new TeamIsoResult
                        {
                            Isos = kvp.Value.Result.ToArray(),
                            TeamId = kvp.Key,
                            TeamName = teams.Where(t => t.Id == kvp.Key).Select(x => x.Name).FirstOrDefault()
                        });
                    }
                }

                return isoResult;
            }
        }
    }
}