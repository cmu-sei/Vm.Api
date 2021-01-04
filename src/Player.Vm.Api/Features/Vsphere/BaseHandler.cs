// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Player.Vm.Api.Infrastructure.Exceptions;
using AutoMapper;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using System.Security.Claims;
using Player.Vm.Api.Infrastructure.Extensions;
using System.Collections.Generic;
using Player.Vm.Api.Domain.Models;
using System.Linq;

namespace Player.Vm.Api.Features.Vsphere
{
    public class BaseHandler
    {
        private readonly IMapper _mapper;
        private readonly IVsphereService _vsphereService;
        private readonly IPlayerService _playerService;
        private readonly Guid _userId;
        private readonly IPermissionsService _permissionsService;
        private readonly IVmService _vmService;


        public BaseHandler(
            IMapper mapper,
            IVsphereService vsphereService,
            IPlayerService playerService,
            IPrincipal principal,
            IPermissionsService permissionsService,
            IVmService vmService)
        {
            _mapper = mapper;
            _vsphereService = vsphereService;
            _playerService = playerService;
            _userId = (principal as ClaimsPrincipal).GetId();
            _permissionsService = permissionsService;
            _vmService = vmService;
        }

        protected async Task<VsphereVirtualMachine> GetVsphereVirtualMachine(Features.Vms.Vm vm, CancellationToken cancellationToken)
        {
            var domainMachine = await _vsphereService.GetMachineById(vm.Id);

            if (domainMachine == null)
                throw new EntityNotFoundException<VsphereVirtualMachine>();

            var vsphereVirtualMachine = _mapper.Map<VsphereVirtualMachine>(domainMachine);
            var canManage = await _playerService.CanManageTeamsAsync(vm.TeamIds, false, cancellationToken);

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

        /// <summary>
        /// Get a Vm and check for appropriate access.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="blockingPermission">A permission that should block the operation from completing, if present</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected async Task<Vms.Vm> GetVm(Guid id, Permissions? blockingPermission = null,
                                           CancellationToken cancellationToken = default)
        {
            var blockingPermissions = new List<Permissions>();

            if (blockingPermission.HasValue)
            {
                blockingPermissions.Add(blockingPermission.Value);
            }

            return await this.GetVm(id, blockingPermissions, cancellationToken);
        }

        protected async Task<Vms.Vm> GetVm(Guid id, IEnumerable<Permissions> blockingPermissions,
                                       CancellationToken cancellationToken)
        {
            var vm = await _vmService.GetAsync(id, cancellationToken);

            if (vm == null)
                throw new EntityNotFoundException<VsphereVirtualMachine>();

            if (blockingPermissions.Any() &&
                (await _permissionsService.GetPermissions(vm.TeamIds, cancellationToken)).Any(x => blockingPermissions.Contains(x)))
            {
                throw new ForbiddenException();
            }

            return vm;
        }
    }
}