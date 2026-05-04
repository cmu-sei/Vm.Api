// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using AutoMapper;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Proxmox
{
    public class GetConsole
    {
        [DataContract(Name = "GetProxmoxConsoleQuery")]
        public class Query : IRequest<ProxmoxConsole>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
        }

        public record ProxmoxConsole
        {
            public string Ticket { get; init; }
            public string Url { get; init; }
        }

        public class Handler : IRequestHandler<Query, ProxmoxConsole>
        {
            private readonly VmContext _db;
            private readonly IMapper _mapper;
            private readonly IVmService _vmService;
            private readonly IProxmoxService _proxmoxService;
            private readonly IXApiService _xApiService;
            private readonly IPlayerService _playerService;

            public Handler(
                VmContext db,
                IMapper mapper,
                IVmService vmService,
                IProxmoxService proxmoxService,
                IXApiService xApiService,
                IPlayerService playerService)
            {
                _db = db;
                _mapper = mapper;
                _vmService = vmService;
                _proxmoxService = proxmoxService;
                _xApiService = xApiService;
                _playerService = playerService;
            }

            public async Task<ProxmoxConsole> Handle(Query request, CancellationToken cancellationToken)
            {
                var vmEntity = await _db.Vms
                    .Include(x => x.VmTeams)
                    .Where(x => x.Id == request.Id)
                    .SingleOrDefaultAsync(cancellationToken);

                if (vmEntity == null)
                    throw new EntityNotFoundException<Vm.Api.Features.Vms.Vm>();

                await _vmService.CanAccessVm(vmEntity, cancellationToken);

                if (vmEntity.ProxmoxVmInfo == null)
                    throw new InvalidOperationException($"VM {request.Id} does not have Proxmox configuration. Ensure ProxmoxVmInfo is set with valid Id and Node values.");

                // Emit xAPI statement for console access
                if (_xApiService.IsConfigured() && vmEntity.VmTeams.Any())
                {
                    try
                    {
                        var firstTeamId = vmEntity.VmTeams.First().TeamId;
                        var team = await _playerService.GetTeamById(firstTeamId);
                        if (team != null)
                        {
                            await _xApiService.EmitVmConsoleAccessedAsync(request.Id, team.ViewId, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail the console request
                        System.Diagnostics.Debug.WriteLine($"xAPI tracking failed: {ex.Message}");
                    }
                }

                return _mapper.Map<ProxmoxConsole>(await _proxmoxService.GetConsole(vmEntity.ProxmoxVmInfo));
            }
        }
    }
}