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
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Features.Proxmox.Commands
{
    /// <summary>
    /// Reverts a Proxmox Vm to the snapshot it is currently running from, the counterpart of vSphere's
    /// revert to current snapshot. Gated exactly as reverting to a named snapshot is.
    /// </summary>
    public class Revert
    {
        [DataContract(Name = "RevertProxmoxVirtualMachine")]
        public class Command : IRequest<string>, ICheckProxmoxTasksRequest
        {
            [JsonIgnore]
            public Guid Id { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, string>
        {
            private readonly IProxmoxService _proxmoxService;

            public Handler(VmContext db, IPlayerService playerService, IVmService vmService, IProxmoxService proxmoxService)
                : base(db, playerService, vmService)
            {
                _proxmoxService = proxmoxService;
            }

            public async Task<string> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await GetVm(request.Id, [AppSystemPermission.RevertVms], [AppViewPermission.RevertVms], [], cancellationToken,
                    "You do not have permission to revert this vm.");
                return await _proxmoxService.RevertToCurrentSnapshot(vm.ProxmoxVmInfo);
            }
        }
    }
}
