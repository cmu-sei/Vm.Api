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
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using AutoMapper;

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

            /// <summary>
            /// The live power state of the virtual machine. When this is anything other than On,
            /// Ticket and Url are null because there is no console to connect to.
            /// </summary>
            public Domain.Models.PowerState PowerState { get; init; }
        }

        public class Handler : BaseHandler, IRequestHandler<Query, ProxmoxConsole>
        {
            private readonly IMapper _mapper;
            private readonly IProxmoxService _proxmoxService;
            public Handler(
                VmContext db,
                IPlayerService playerService,
                IVmService vmService,
                IMapper mapper,
                IProxmoxService proxmoxService)
                : base(db, playerService, vmService)
            {
                _mapper = mapper;
                _proxmoxService = proxmoxService;
            }

            public async Task<ProxmoxConsole> Handle(Query request, CancellationToken cancellationToken)
            {
                var vm = await GetVm(request.Id, [], [], [], cancellationToken);
                var console = _mapper.Map<ProxmoxConsole>(
                    await _proxmoxService.GetConsole(vm.ProxmoxVmInfo));

                return console;
            }
        }
    }
}
