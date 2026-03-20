// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Player.Vm.Api.Infrastructure.Exceptions;
using AutoMapper;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Domain.Vsphere.Extensions;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Vsphere
{
    public class ChangeNetwork
    {
        [DataContract(Name = "ChangeVsphereVirtualMachineNetwork")]
        public class Command : IRequest<VsphereVirtualMachine>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string Adapter { get; set; }
            public string Network { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, VsphereVirtualMachine>
        {
            private readonly IVsphereService _vsphereService;
            private readonly IVmService _vmService;
            private readonly IMapper _mapper;
            private readonly IViewService _viewService;

            public Handler(
                IVsphereService vsphereService,
                IVmService vmService,
                IMapper mapper,
                IPlayerService playerService,
                IViewService viewService,
                IPrincipal principal) :
                base(mapper, vsphereService, playerService, principal, vmService, viewService)
            {
                _vsphereService = vsphereService;
                _vmService = vmService;
                _mapper = mapper;
                _viewService = viewService;
            }

            public async Task<VsphereVirtualMachine> Handle(Command request, CancellationToken cancellationToken)
            {
                // Get VM with basic access check
                var vm = await _vmService.GetAsync(request.Id, cancellationToken);

                if (vm == null)
                    throw new EntityNotFoundException<VsphereVirtualMachine>();

                var connectionAddress = await _vsphereService.GetConnectionAddress(vm.Id);

                var viewIds = await _viewService.GetViewIdsForTeams(vm.TeamIds, cancellationToken);
                var viewId = viewIds.FirstOrDefault();

                var effectivePerms = await _vmService.GetEffectiveNetworkPermissions(
                    viewId, vm.TeamIds,
                    VmType.Vsphere, connectionAddress,
                    cancellationToken);

                if (effectivePerms.AllowedNetworks?.Count > 0)
                {
                    if (!effectivePerms.AllowedNetworks.ContainsKey(request.Network))
                        throw new ForbiddenException("The target network is not in your allowed networks list");

                    // Validate stored name matches actual vSphere network name
                    var storedName = effectivePerms.AllowedNetworks[request.Network];
                    if (storedName != null)
                    {
                        var machine = await _vsphereService.GetMachineById(vm.Id);
                        var validated = await _vsphereService.GetVmNetworks(machine, false, effectivePerms.AllowedNetworks);
                        if (!validated.ContainsKey(request.Network))
                            throw new ForbiddenException("Network name mismatch — the registered network may have been renamed or misconfigured");
                    }
                }
                else
                {
                    throw new ForbiddenException("You do not have permission to change networks on this VM");
                }

                await _vsphereService.ReconfigureVm(request.Id, Feature.net, request.Adapter, request.Network);

                return await base.GetVsphereVirtualMachine(vm, cancellationToken);
            }
        }
    }
}
