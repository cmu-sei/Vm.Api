// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
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
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Files;

namespace Player.Vm.Api.Features.Vsphere
{
    public class MountIso
    {
        [DataContract(Name = "MountVsphereIso")]
        public class Command : IRequest<VsphereVirtualMachine>
        {
            [JsonIgnore]
            public Guid Id { get; set; }

            /// <summary>
            /// The datastore path of the ISO to mount, as returned in the MountValue of
            /// GET vms/vsphere/{id}/isos. Only paths within an ISO folder belonging to a View and team
            /// of this Vm are accepted.
            /// </summary>
            public string Iso { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, VsphereVirtualMachine>
        {
            private readonly IVsphereService _vsphereService;
            private readonly IVmService _vmService;
            private readonly IMapper _mapper;
            private readonly IIsoService _isoService;

            public Handler(
                IVsphereService vsphereService,
                IVmService vmService,
                IMapper mapper,
                IPlayerService playerService,
                IPrincipal principal,
                IViewService viewService,
                IIsoService isoService) :
                base(mapper, vsphereService, playerService, principal, vmService, viewService)
            {
                _vsphereService = vsphereService;
                _vmService = vmService;
                _mapper = mapper;
                _isoService = isoService;
            }

            public async Task<VsphereVirtualMachine> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await base.GetVmForEditing(request.Id, cancellationToken);

                if (string.IsNullOrWhiteSpace(request.Iso))
                    throw new BadRequestException("An iso is required");

                // The path goes straight into the Vm's cdrom backing, and it can name any file the
                // datastore will serve - including another View's or team's ISO. Edit rights on THIS Vm
                // are not permission to read THAT file, so the path is never taken on trust: the scope it
                // encodes has to belong to this Vm and the caller has to hold edit rights over that
                // scope. What gets mounted is the path rebuilt from that scope, not the one submitted.
                var iso = await _isoService.ResolveMountValueAsync(
                    vm.Id, VmType.Vsphere, vm.TeamIds, request.Iso, cancellationToken);

                await _vsphereService.ReconfigureVm(request.Id, Feature.iso, "", iso);

                return await base.GetVsphereVirtualMachine(vm, cancellationToken);
            }
        }
    }
}