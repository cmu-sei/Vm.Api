// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Proxmox.Commands
{
    public class CreateSnapshot
    {
        [DataContract(Name = "CreateProxmoxVirtualMachineSnapshot")]
        public class Command : IRequest<string>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string SnapshotName { get; set; }
            public string Description { get; set; }
            public bool IncludeRam { get; set; }
        }

        // PVE's own rule for a snapshot name (format pve-configid, maxLength 40), checked here so a bad
        // name is a 400 with a reason instead of the 500 PVE's rejection would otherwise surface as.
        // "current" is PVE's pseudo-snapshot for the running state, so it cannot be taken.
        private static readonly Regex ValidName = new("^[A-Za-z][A-Za-z0-9_-]{1,39}$", RegexOptions.CultureInvariant);

        public static string Validate(string snapshotName)
        {
            if (string.IsNullOrWhiteSpace(snapshotName))
                return "A snapshot name is required.";

            if (!ValidName.IsMatch(snapshotName))
                return "A snapshot name must be 2 to 40 characters, start with a letter, and contain only letters, digits, hyphens and underscores.";

            if (string.Equals(snapshotName, "current", StringComparison.OrdinalIgnoreCase))
                return "\"current\" is reserved by Proxmox and cannot be used as a snapshot name.";

            return null;
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
                    "You do not have permission to manage snapshots for this vm.");

                var problem = Validate(request.SnapshotName);
                if (problem != null)
                    throw new BadRequestException(problem);

                return await _proxmoxService.CreateSnapshot(
                    vm.ProxmoxVmInfo,
                    request.SnapshotName,
                    request.Description,
                    request.IncludeRam);
            }
        }
    }
}
