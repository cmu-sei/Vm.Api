// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Proxmox.Options;
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
            public string ProgramPath { get; set; }
            public string Arguments { get; set; }
            public string WorkingDirectory { get; set; }
            public int? TimeoutSeconds { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, GuestProcessResult>
        {
            private readonly IProxmoxService _proxmoxService;
            private readonly ProxmoxOptions _options;

            public Handler(VmContext db, IPlayerService playerService, IProxmoxService proxmoxService, ProxmoxOptions options)
                : base(db, playerService)
            {
                _proxmoxService = proxmoxService;
                _options = options;
            }

            public async Task<GuestProcessResult> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await GetVmForEditing(request.Id, cancellationToken);
                var timeout = request.TimeoutSeconds.HasValue
                    ? TimeSpan.FromSeconds(request.TimeoutSeconds.Value)
                    : TimeSpan.FromSeconds(_options.GuestProcessDefaultTimeoutSeconds);

                return await _proxmoxService.RunGuestProcess(
                    vm.ProxmoxVmInfo,
                    request.ProgramPath,
                    request.Arguments,
                    timeout);
            }
        }
    }
}
