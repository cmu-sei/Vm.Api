// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Infrastructure.Exceptions;
using AutoMapper;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using System.Security.Claims;
using Player.Vm.Api.Infrastructure.Extensions;
using System.Linq;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Features.Vsphere
{
    public class BaseHandler
    {
        private readonly IMapper _mapper;
        private readonly IVsphereService _vsphereService;
        protected readonly IPlayerService _playerService;
        private readonly Guid _userId;
        private readonly IVmService _vmService;


        public BaseHandler(
            IMapper mapper,
            IVsphereService vsphereService,
            IPlayerService playerService,
            IPrincipal principal,
            IVmService vmService)
        {
            _mapper = mapper;
            _vsphereService = vsphereService;
            _playerService = playerService;
            _userId = (principal as ClaimsPrincipal).GetId();
            _vmService = vmService;
        }

        protected async Task<VsphereVirtualMachine> GetVsphereVirtualMachine(Features.Vms.Vm vm, CancellationToken cancellationToken)
        {
            var domainMachine = await _vsphereService.GetMachineById(vm.Id);

            if (domainMachine == null)
                throw new EntityNotFoundException<VsphereVirtualMachine>();

            var vsphereVirtualMachine = _mapper.Map<VsphereVirtualMachine>(domainMachine);
            var canManage = await _playerService.CanManageTeams(vm.TeamIds, cancellationToken);

            vsphereVirtualMachine.Ticket = await _vsphereService.GetConsoleUrl(domainMachine); ;
            vsphereVirtualMachine.NetworkCards = await _vsphereService.GetNicOptions(
                id: vm.Id,
                canManage: canManage,
                allowedNetworks: vm.AllowedNetworks,
                machine: domainMachine);

            // copy vm properties
            vsphereVirtualMachine = _mapper.Map(vm, vsphereVirtualMachine);
            vsphereVirtualMachine.CanAccessNicConfiguration = canManage;
            vsphereVirtualMachine.IsOwner = vsphereVirtualMachine.UserId == _userId;

            return vsphereVirtualMachine;
        }

        protected async Task<Vms.Vm> GetVmForEditing(Guid id, CancellationToken cancellationToken)
        {
            return await this.GetVm(id,
                                    [AppSystemPermission.EditViews],
                                    [AppViewPermission.EditView],
                                    [AppTeamPermission.EditTeam],
                                    cancellationToken,
                                    "You do not have permission to edit this Vm");
        }

        /// <summary>
        /// Get a Vm and check for appropriate access.
        /// </summary>
        /// <returns></returns>
        protected async Task<Vms.Vm> GetVm(Guid id,
                                           AppSystemPermission[] requiredSystemPermissions,
                                           AppViewPermission[] requiredViewPermissions,
                                           AppTeamPermission[] requiredTeamPermissions,
                                           CancellationToken cancellationToken,
                                           string errorMessage = "You do not have permission to perform this action")
        {
            var vm = await _vmService.GetAsync(id, cancellationToken);

            if (vm == null)
                throw new EntityNotFoundException<VsphereVirtualMachine>();

            if (requiredSystemPermissions.Any() || requiredViewPermissions.Any() || requiredTeamPermissions.Any())
            {
                if (!await _playerService.Can(vm.TeamIds, [], requiredSystemPermissions, requiredViewPermissions, requiredTeamPermissions, cancellationToken))
                    throw new ForbiddenException(errorMessage);
            }

            return vm;
        }
    }
}