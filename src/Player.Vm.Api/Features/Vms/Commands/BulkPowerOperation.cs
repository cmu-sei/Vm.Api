// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Domain.Vsphere.Models;
using System.Text.Json.Serialization;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using System.Collections.Generic;
using Player.Vm.Api.Features.Shared.Interfaces;

namespace Player.Vm.Api.Features.Vms
{
    public class BulkPowerOperation
    {
        [DataContract(Name = "BulkPowerOperation")]
        public class Command : IRequest<Response>, ICheckTasksRequest
        {
            public Guid[] Ids { get; set; }

            [JsonIgnore]
            public PowerOperation Operation { get; set; }
        }

        [DataContract(Name = "BulkPowerOperationResponse")]
        public class Response
        {
            public Guid[] Accepted { get; set; }

            // TODO: Change key to Guid when System.Text.Json
            // adds support for non-string Dictionary keys (.NET 5?)
            public Dictionary<string, string> Errors { get; set; }
        }

        public class Handler : IRequestHandler<Command, Response>
        {
            private readonly IVsphereService _vsphereService;
            private readonly IPlayerService _playerService;
            private readonly VmContext _dbContext;
            private readonly IVmService _vmService;

            public Handler(
                IVsphereService vsphereService,
                IPlayerService playerService,
                VmContext dbContext,
                IVmService vmService)
            {
                _vsphereService = vsphereService;
                _playerService = playerService;
                _dbContext = dbContext;
                _vmService = vmService;
            }

            public async Task<Response> Handle(Command request, CancellationToken cancellationToken)
            {
                var errorsDict = new Dictionary<Guid, string>();
                var acceptedList = new List<Guid>();

                var vms = await _dbContext.Vms
                    .Include(x => x.VmTeams)
                    .Where(x => request.Ids.Contains(x.Id))
                    .ToListAsync(cancellationToken);

                foreach (var id in request.Ids)
                {
                    var vm = vms.Where(x => x.Id == id).FirstOrDefault();

                    if (vm == null)
                    {
                        errorsDict.Add(id, "Virtual Machine Not Found");
                    }
                    else if (vm.PowerState == PowerState.Unknown || vm.Type != VmType.Vsphere)
                    {
                        errorsDict.Add(id, "Unsupported Operation");
                    }
                    else if (!await _vmService.CanAccessVm(vm, cancellationToken))
                    {
                        errorsDict.Add(id, "Unauthorized");
                    }
                    else if (!await _playerService.CanEditTeams(vm.VmTeams.Select(x => x.TeamId), cancellationToken))
                    {
                        errorsDict.Add(id, "Insufficient Permissions");
                    }
                    else
                    {
                        acceptedList.Add(id);
                    }
                }

                foreach (var vm in vms.Where(x => acceptedList.Contains(x.Id)))
                {
                    vm.HasPendingTasks = true;
                }

                await _dbContext.SaveChangesAsync();

                if (request.Operation == PowerOperation.Shutdown)
                {
                    var results = await _vsphereService.BulkShutdown(acceptedList.ToArray());

                    errorsDict = errorsDict
                        .Concat(results)
                        .ToLookup(x => x.Key, x => x.Value)
                        .ToDictionary(x => x.Key, g => g.First());
                }
                else if (request.Operation == PowerOperation.Reboot)
                {
                    var results = await _vsphereService.BulkReboot(acceptedList.ToArray());

                    errorsDict = errorsDict
                        .Concat(results)
                        .ToLookup(x => x.Key, x => x.Value)
                        .ToDictionary(x => x.Key, g => g.First());
                }
                else
                {
                    await _vsphereService.BulkPowerOperation(acceptedList.ToArray(), request.Operation);
                }

                return new Response
                {
                    Accepted = acceptedList.ToArray(),
                    Errors = errorsDict.Where(x => !string.IsNullOrEmpty(x.Value)).ToDictionary(x => x.Key.ToString(), y => y.Value)
                };
            }
        }
    }
}