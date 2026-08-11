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
            /// GET vms/vsphere/{id}/isos. Only values from that listing are accepted.
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
                // are not permission to read THAT file, so the path is never taken on trust: it has to
                // be one of the paths this caller's own listing for this Vm just returned, which is
                // already scoped to the Views and teams they can see.
                var viewTeams = await _isoService.ResolveViewTeamsForVmAsync(vm.TeamIds, cancellationToken);

                var allowed = await _isoService.BuildVmIsoResultsAsync(
                    vm.Id, VmType.Vsphere, viewTeams, cancellationToken);

                var isAllowed = allowed
                    .SelectMany(result => result.Isos.Concat(result.TeamIsoResults.SelectMany(t => t.Isos)))
                    .Any(iso => string.Equals(iso.MountValue, request.Iso, StringComparison.Ordinal));

                if (!isAllowed)
                    throw new ForbiddenException("The specified iso is not available to this Vm");

                await _vsphereService.ReconfigureVm(request.Id, Feature.iso, "", request.Iso);

                return await base.GetVsphereVirtualMachine(vm, cancellationToken);
            }
        }
    }
}