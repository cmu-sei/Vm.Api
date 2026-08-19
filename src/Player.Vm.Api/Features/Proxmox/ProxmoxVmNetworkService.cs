// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Features.Networks;
using DomainVm = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Features.Proxmox
{
    /// <summary>
    /// The view-network permissions that apply to a Proxmox Vm, along with the view they were
    /// resolved from. The view id is carried here so that building the response does not have to
    /// resolve it a second time.
    /// </summary>
    public record ProxmoxNetworkPermissions(Guid ViewId, EffectiveNetworkPermission Permissions);

    public interface IProxmoxVmNetworkService
    {
        /// <summary>
        /// Resolves the view-network permissions the calling user has for the given Vm.
        /// </summary>
        Task<ProxmoxNetworkPermissions> GetPermissions(DomainVm vm, CancellationToken ct);

        /// <summary>
        /// Builds the API representation of a Proxmox Vm, reading its live NIC configuration and
        /// restricting the selectable networks to those the caller is permitted to use.
        /// </summary>
        Task<ProxmoxVirtualMachine> ToResponse(
            DomainVm vm,
            ProxmoxNetworkPermissions permissions,
            CancellationToken ct);
    }

    public class ProxmoxVmNetworkService : IProxmoxVmNetworkService
    {
        private readonly IViewService _viewService;
        private readonly INetworkService _networkService;
        private readonly IProxmoxService _proxmoxService;
        private readonly ProxmoxOptions _proxmoxOptions;
        private readonly bool _isoProviderEnabled;

        public ProxmoxVmNetworkService(
            IViewService viewService,
            INetworkService networkService,
            IProxmoxService proxmoxService,
            ProxmoxOptions proxmoxOptions,
            IEnumerable<IIsoProvider> isoProviders)
        {
            _viewService = viewService;
            _networkService = networkService;
            _proxmoxService = proxmoxService;
            _proxmoxOptions = proxmoxOptions;

            // Asked of the provider rather than recomputed from ProxmoxOptions, so there is exactly one
            // definition of "Proxmox ISO support is on" and this cannot drift from it.
            _isoProviderEnabled = isoProviders.Any(p => p.ProviderType == VmType.Proxmox && p.Enabled);
        }

        public async Task<ProxmoxNetworkPermissions> GetPermissions(DomainVm vm, CancellationToken ct)
        {
            var teamIds = vm.VmTeams.Select(x => x.TeamId).ToArray();
            var viewId = (await _viewService.GetViewIdsForTeams(teamIds, ct)).FirstOrDefault();

            var permissions = await _networkService.GetEffectiveNetworkPermissions(
                viewId,
                teamIds,
                VmType.Proxmox,
                _proxmoxOptions.Host,
                ct);

            return new ProxmoxNetworkPermissions(viewId, permissions);
        }

        public async Task<ProxmoxVirtualMachine> ToResponse(
            DomainVm vm,
            ProxmoxNetworkPermissions permissions,
            CancellationToken ct)
        {
            var allowedNetworks = permissions.Permissions.AllowedNetworks ?? new();
            var config = await _proxmoxService.GetVmConfigSummary(vm.ProxmoxVmInfo, ct);
            var currentNetworks = config.CurrentNetworks;

            // A Vm can already sit on a network the caller is not allowed to select. Those are still
            // shown, by name where one is registered, but as read-only options.
            var unauthorizedCurrentNetworks = currentNetworks.Values
                .Where(network => !string.IsNullOrWhiteSpace(network) && !allowedNetworks.ContainsKey(network))
                .Distinct()
                .ToArray();

            var networkNames = await _networkService.GetNetworkNames(
                permissions.ViewId,
                VmType.Proxmox,
                _proxmoxOptions.Host,
                unauthorizedCurrentNetworks,
                ct);

            return new ProxmoxVirtualMachine
            {
                Id = vm.Id,
                Name = vm.Name,
                UserId = vm.UserId,
                NetworkCards = _proxmoxService.GetNicOptions(
                    currentNetworks,
                    allowedNetworks,
                    networkNames),
                CanAccessNicConfiguration = allowedNetworks.Count > 0,
                // Drive presence included, not just the Vm type: Proxmox cannot hot-add an IDE drive, so
                // a QEMU Vm built without one can never accept a mount and offering the control would only
                // produce a 400 once the picker had already been filled in.
                CanMountIso = _isoProviderEnabled
                    && vm.ProxmoxVmInfo.Type == ProxmoxVmType.QEMU
                    && config.HasCdromDrive
            };
        }
    }
}
