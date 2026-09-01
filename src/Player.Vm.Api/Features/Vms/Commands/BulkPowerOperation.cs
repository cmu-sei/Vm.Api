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
using System.Text.Json.Serialization;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using System.Collections.Generic;
using Player.Vm.Api.Features.Shared.Interfaces;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Vms
{
    public class BulkPowerOperation
    {
        [DataContract(Name = "BulkPowerOperation")]
        public class Command : IRequest<Response>, ICheckVsphereTasksRequest, ICheckProxmoxTasksRequest
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
            private readonly IProxmoxService _proxmoxService;
            private readonly IPlayerService _playerService;
            private readonly VmContext _dbContext;
            private readonly IVmService _vmService;

            public Handler(
                IVsphereService vsphereService,
                IProxmoxService proxmoxService,
                IPlayerService playerService,
                VmContext dbContext,
                IVmService vmService)
            {
                _vsphereService = vsphereService;
                _proxmoxService = proxmoxService;
                _playerService = playerService;
                _dbContext = dbContext;
                _vmService = vmService;
            }

            public async Task<Response> Handle(Command request, CancellationToken cancellationToken)
            {
                var errorsDict = new Dictionary<Guid, string>();
                var vsphereAccepted = new List<Guid>();
                var proxmoxAccepted = new List<Guid>();

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
                        continue;
                    }

                    if (vm.Type != VmType.Vsphere && vm.Type != VmType.Proxmox)
                    {
                        errorsDict.Add(id, "Unsupported Operation");
                        continue;
                    }

                    // Only vSphere gates on PowerState. ProxmoxExtensions.GetPowerState reports
                    // Unknown for a healthy running Vm any time the state poller hasn't run yet -
                    // during the first refresh interval, or while Proxmox is disabled - so gating
                    // on it would reject a whole batch on the exact operation meant to fix it.
                    if (vm.Type == VmType.Vsphere && vm.PowerState == PowerState.Unknown)
                    {
                        errorsDict.Add(id, "Unsupported Operation");
                        continue;
                    }

                    // Revert needs HasSnapshot, which nothing populates for Proxmox yet.
                    if (vm.Type == VmType.Proxmox && request.Operation == PowerOperation.Revert)
                    {
                        errorsDict.Add(id, "Unsupported Operation");
                        continue;
                    }

                    if (!await TryCanAccessVm(vm, cancellationToken))
                    {
                        errorsDict.Add(id, "Unauthorized");
                        continue;
                    }

                    if (!await CanPerformOperation(vm, request.Operation, cancellationToken))
                    {
                        errorsDict.Add(id, "Insufficient Permissions");
                        continue;
                    }

                    if (request.Operation == PowerOperation.Revert && !vm.HasSnapshot)
                    {
                        errorsDict.Add(id, "Virtual Machine does not have a snapshot");
                        continue;
                    }

                    if (vm.Type == VmType.Proxmox)
                    {
                        proxmoxAccepted.Add(id);
                    }
                    else
                    {
                        vsphereAccepted.Add(id);
                    }
                }

                var acceptedList = vsphereAccepted.Concat(proxmoxAccepted).ToList();

                foreach (var vm in vms.Where(x => acceptedList.Contains(x.Id)))
                {
                    vm.HasPendingTasks = true;
                }

                await _dbContext.SaveChangesAsync();

                if (vsphereAccepted.Count > 0)
                {
                    if (request.Operation == PowerOperation.Shutdown)
                    {
                        errorsDict = MergeErrors(errorsDict, await _vsphereService.BulkShutdown(vsphereAccepted.ToArray()));
                    }
                    else if (request.Operation == PowerOperation.Reboot)
                    {
                        errorsDict = MergeErrors(errorsDict, await _vsphereService.BulkReboot(vsphereAccepted.ToArray()));
                    }
                    else
                    {
                        errorsDict = MergeErrors(errorsDict,
                            await _vsphereService.BulkPowerOperation(vsphereAccepted.ToArray(), request.Operation));
                    }
                }

                if (proxmoxAccepted.Count > 0)
                {
                    errorsDict = MergeErrors(errorsDict,
                        await _proxmoxService.BulkPowerOperation(proxmoxAccepted.ToArray(), request.Operation));
                }

                return new Response
                {
                    Accepted = acceptedList.ToArray(),
                    Errors = errorsDict.Where(x => !string.IsNullOrEmpty(x.Value)).ToDictionary(x => x.Key.ToString(), y => y.Value)
                };
            }

            private static Dictionary<Guid, string> MergeErrors(
                Dictionary<Guid, string> errors,
                Dictionary<Guid, string> results)
            {
                return errors
                    .Concat(results)
                    .ToLookup(x => x.Key, x => x.Value)
                    .ToDictionary(x => x.Key, g => g.First());
            }

            /// <summary>
            /// IVmService.CanAccessVm throws rather than returning false, which is right for its
            /// other callers but would fail this whole batch over a single inaccessible Vm. Catch
            /// here so an unauthorized Vm becomes one entry in Errors and the rest still run.
            /// </summary>
            private async Task<bool> TryCanAccessVm(Domain.Models.Vm vm, CancellationToken cancellationToken)
            {
                try
                {
                    return await _vmService.CanAccessVm(vm, cancellationToken);
                }
                catch (ForbiddenException)
                {
                    return false;
                }
                catch (EntityNotFoundException<Vm>)
                {
                    return false;
                }
            }

            private async Task<bool> CanPerformOperation(Domain.Models.Vm vm, PowerOperation operation, CancellationToken cancellationToken)
            {
                if (operation == PowerOperation.Revert)
                {
                    return await _playerService.Can(vm.VmTeams.Select(x => x.TeamId), [], [], [AppViewPermission.RevertVms], [], cancellationToken);
                }
                else
                {
                    return await _playerService.CanEditTeams(vm.VmTeams.Select(x => x.TeamId), cancellationToken);
                }
            }
        }
    }
}