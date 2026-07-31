// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Proxmox;

public abstract class NetworkBaseHandler
{
    private readonly ProxmoxOptions _proxmoxOptions;

    protected NetworkBaseHandler(
        IVmService vmService,
        IViewService viewService,
        INetworkService networkService,
        IProxmoxService proxmoxService,
        ProxmoxOptions proxmoxOptions)
    {
        VmService = vmService;
        ViewService = viewService;
        NetworkService = networkService;
        ProxmoxService = proxmoxService;
        _proxmoxOptions = proxmoxOptions;
    }

    protected IVmService VmService { get; }
    protected IViewService ViewService { get; }
    protected INetworkService NetworkService { get; }
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

    protected async Task<(Vms.Vm Vm, Guid ViewId, EffectiveNetworkPermission Permissions)> GetVmAndPermissions(
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

        return (vm, viewId, permissions);
    }

    protected async Task<ProxmoxVirtualMachine> BuildVm(
        Vms.Vm vm,
        Guid viewId,
        EffectiveNetworkPermission permissions,
        CancellationToken cancellationToken)
    {
        var allowedNetworks = permissions.AllowedNetworks ?? new();
        var info = ToDomainInfo(vm.ProxmoxVmInfo, vm.Id);
        var currentNetworks = await ProxmoxService.GetCurrentNetworks(info, cancellationToken);
        var unauthorizedCurrentNetworks = currentNetworks.Values
            .Where(network => !string.IsNullOrWhiteSpace(network) && !allowedNetworks.ContainsKey(network))
            .Distinct()
            .ToArray();
        var networkNames = await NetworkService.GetNetworkNames(
            viewId,
            VmType.Proxmox,
            _proxmoxOptions.Host,
            unauthorizedCurrentNetworks,
            cancellationToken);

        return new ProxmoxVirtualMachine
        {
            Id = vm.Id,
            Name = vm.Name,
            UserId = vm.UserId,
            NetworkCards = ProxmoxService.GetNicOptions(
                currentNetworks,
                allowedNetworks,
                networkNames),
            CanAccessNicConfiguration = allowedNetworks.Count > 0
        };
    }
}
