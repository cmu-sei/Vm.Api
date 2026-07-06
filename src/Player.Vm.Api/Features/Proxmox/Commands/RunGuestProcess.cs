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
using Player.Vm.Api.Domain.Vsphere.Models;

namespace Player.Vm.Api.Features.Proxmox.Commands
{
    public class RunGuestProcess
    {
        [DataContract(Name = "RunGuestProcessOnProxmoxVirtualMachine")]
        public class Command : IRequest<GuestProcessResult>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            // Username/Password retained for vSphere parity; QGA runs as root and ignores them.
            public string Username { get; set; }
            public string Password { get; set; }
            public string ProgramPath { get; set; }
            public string Arguments { get; set; }
            public string WorkingDirectory { get; set; }
            public int? TimeoutSeconds { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, GuestProcessResult>
        {
            private readonly IProxmoxService _proxmoxService;

            public Handler(VmContext db, IPlayerService playerService, IProxmoxService proxmoxService)
                : base(db, playerService)
            {
                _proxmoxService = proxmoxService;
            }

            public async Task<GuestProcessResult> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await GetVmForEditing(request.Id, cancellationToken);
                var timeout = request.TimeoutSeconds.HasValue
                    ? TimeSpan.FromSeconds(request.TimeoutSeconds.Value)
                    : TimeSpan.FromMinutes(5);

                return await _proxmoxService.RunGuestProcess(
                    vm.ProxmoxVmInfo,
                    request.ProgramPath,
                    request.Arguments,
                    timeout);
            }
        }
    }
}
