// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Data;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;
using DomainVm = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Features.Proxmox
{
    public class BaseHandler
    {
        private readonly VmContext _db;
        private readonly Player.Vm.Api.Domain.Services.IPlayerService _playerService;

        public BaseHandler(VmContext db, Player.Vm.Api.Domain.Services.IPlayerService playerService)
        {
            _db = db;
            _playerService = playerService;
        }

        protected async Task<DomainVm> GetVmForEditing(Guid id, CancellationToken cancellationToken)
        {
            return await GetVm(id,
                [AppSystemPermission.EditViews],
                [AppViewPermission.EditView],
                [AppTeamPermission.EditTeam],
                cancellationToken,
                "You do not have permission to edit this Vm");
        }

        protected async Task<DomainVm> GetVm(
            Guid id,
            AppSystemPermission[] requiredSystemPermissions,
            AppViewPermission[] requiredViewPermissions,
            AppTeamPermission[] requiredTeamPermissions,
            CancellationToken cancellationToken,
            string errorMessage = "You do not have permission to perform this action")
        {
            var vm = await _db.Vms
                .Include(x => x.VmTeams)
                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

            if (vm == null)
                throw new EntityNotFoundException<Player.Vm.Api.Features.Vms.Vm>();

            if (vm.ProxmoxVmInfo == null)
                throw new ForbiddenException("This action is only valid for Proxmox VMs");

            if (requiredSystemPermissions.Length > 0 || requiredViewPermissions.Length > 0 || requiredTeamPermissions.Length > 0)
            {
                var teamIds = new List<Guid>();
                foreach (var vt in vm.VmTeams)
                    teamIds.Add(vt.TeamId);

                if (!await _playerService.Can(teamIds, [], requiredSystemPermissions, requiredViewPermissions, requiredTeamPermissions, cancellationToken))
                    throw new ForbiddenException(errorMessage);
            }

            return vm;
        }
    }
}
