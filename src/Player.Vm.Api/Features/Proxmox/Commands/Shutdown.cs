// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Shared.Interfaces;

namespace Player.Vm.Api.Features.Proxmox.Commands
{
    public class Shutdown
    {
        [DataContract(Name = "ShutdownProxmoxVirtualMachine")]
        public class Command : IRequest<string>, ICheckTasksRequest
        {
            [JsonIgnore]
            public Guid Id { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, string>
        {
            private readonly IProxmoxService _proxmoxService;

            public Handler(VmContext db, IPlayerService playerService, IProxmoxService proxmoxService)
                : base(db, playerService)
            {
                _proxmoxService = proxmoxService;
            }

            public async Task<string> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await GetVmForEditing(request.Id, cancellationToken);
                return await _proxmoxService.ShutdownVm(vm.ProxmoxVmInfo);
            }
        }
    }
}
