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
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Features.Proxmox.Commands
{
    public class RevertSnapshot
    {
        [DataContract(Name = "RevertProxmoxVirtualMachineSnapshot")]
        public class Command : IRequest<string>, ICheckTasksRequest
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string SnapshotName { get; set; }
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
                var vm = await GetVm(request.Id, [], [AppViewPermission.RevertVms], [], cancellationToken,
                    "You do not have permission to revert this vm.");
                return await _proxmoxService.RevertSnapshot(vm.ProxmoxVmInfo, request.SnapshotName);
            }
        }
    }
}
