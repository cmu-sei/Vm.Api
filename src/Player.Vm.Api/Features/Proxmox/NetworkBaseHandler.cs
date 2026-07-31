// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Extensions;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Proxmox;

public abstract class NetworkBaseHandler
{
    private readonly IPrincipal _principal;
    private readonly ProxmoxOptions _proxmoxOptions;

    protected NetworkBaseHandler(
        IVmService vmService,
        IViewService viewService,
        IProxmoxService proxmoxService,
        IPrincipal principal,
        ProxmoxOptions proxmoxOptions)
    {
        VmService = vmService;
        ViewService = viewService;
        ProxmoxService = proxmoxService;
        _principal = principal;
        _proxmoxOptions = proxmoxOptions;
    }

    protected IVmService VmService { get; }
    protected IViewService ViewService { get; }
    protected IProxmoxService ProxmoxService { get; }

    protected static Domain.Models.ProxmoxVmInfo ToDomainInfo(Vms.ProxmoxVmInfo info, Guid vmId)
    {
        return new Domain.Models.ProxmoxVmInfo
        {
            VmId = vmId,
            Id = info.Id,
            Node = info.Node,
            Type = (Domain.Models.ProxmoxVmType)info.Type
        };
    }

    protected async Task<(Vms.Vm Vm, EffectiveNetworkPermission Permissions)> GetVmAndPermissions(
        Guid id,
        CancellationToken cancellationToken)
    {
        var vm = await VmService.GetAsync(id, cancellationToken);
        if (vm.ProxmoxVmInfo == null)
            throw new ForbiddenException("This action is only valid for Proxmox VMs");

        var viewId = (await ViewService.GetViewIdsForTeams(vm.TeamIds, cancellationToken)).FirstOrDefault();
        var permissions = await VmService.GetEffectiveNetworkPermissions(
            viewId,
            vm.TeamIds,
            VmType.Proxmox,
            _proxmoxOptions.Host,
            cancellationToken);

        return (vm, permissions);
    }

    protected async Task<ProxmoxVirtualMachine> BuildVm(
        Vms.Vm vm,
        EffectiveNetworkPermission permissions,
        CancellationToken cancellationToken)
    {
        var principal = _principal as ClaimsPrincipal;
        var allowedNetworks = permissions.AllowedNetworks ?? new();

        return new ProxmoxVirtualMachine
        {
            Id = vm.Id,
            Name = vm.Name,
            UserId = vm.UserId,
            IsOwner = vm.UserId == principal.GetId(),
            NetworkCards = await ProxmoxService.GetNicOptions(
                ToDomainInfo(vm.ProxmoxVmInfo, vm.Id),
                allowedNetworks,
                cancellationToken),
            CanAccessNicConfiguration = allowedNetworks.Count > 0
        };
    }
}
