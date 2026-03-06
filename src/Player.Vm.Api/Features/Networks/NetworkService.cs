// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Vm.Api.Features.Networks
{
    public interface INetworkService
    {
        Task<ViewNetworkDto[]> GetByViewId(Guid viewId, CancellationToken ct);
        Task<ViewNetworkDto> CreateViewNetwork(Guid viewId, CreateViewNetworkForm form, CancellationToken ct);
        Task DeleteViewNetwork(Guid viewId, Guid id, CancellationToken ct);
        Task<ViewNetworkDto> UpdateViewNetworkTeams(Guid viewId, Guid id, UpdateViewNetworkTeamsForm form, CancellationToken ct);
        Task<EffectiveNetworkPermission> GetEffectiveNetworkPermissions(
            Guid viewId, IEnumerable<Guid> vmTeamIds, string[] vmAllowedNetworks,
            VmType providerType, string providerInstanceId,
            CancellationToken ct);
    }

    public class NetworkService : INetworkService
    {
        private readonly VmContext _context;
        private readonly IPlayerService _playerService;

        public NetworkService(VmContext context, IPlayerService playerService)
        {
            _context = context;
            _playerService = playerService;
        }

        public async Task<ViewNetworkDto[]> GetByViewId(Guid viewId, CancellationToken ct)
        {
            if (!await CanManageView(viewId, ct))
                throw new ForbiddenException();

            var entities = await _context.ViewNetworks
                .Where(n => n.ViewId == viewId)
                .ToArrayAsync(ct);

            return entities.Select(MapToDto).ToArray();
        }

        public async Task<ViewNetworkDto> CreateViewNetwork(Guid viewId, CreateViewNetworkForm form, CancellationToken ct)
        {
            if (!await CanManageView(viewId, ct))
                throw new ForbiddenException();

            var existing = await _context.ViewNetworks
                .Where(n => n.ViewId == viewId
                    && n.ProviderType == form.ProviderType
                    && n.ProviderInstanceId == form.ProviderInstanceId
                    && n.NetworkId == form.NetworkId)
                .SingleOrDefaultAsync(ct);

            if (existing != null)
                return MapToDto(existing);

            var entity = new ViewNetwork
            {
                ViewId = viewId,
                ProviderType = form.ProviderType,
                ProviderInstanceId = form.ProviderInstanceId,
                NetworkId = form.NetworkId,
                TeamIds = []
            };

            _context.ViewNetworks.Add(entity);
            await _context.SaveChangesAsync(ct);

            return MapToDto(entity);
        }

        public async Task DeleteViewNetwork(Guid viewId, Guid id, CancellationToken ct)
        {
            if (!await CanManageView(viewId, ct))
                throw new ForbiddenException();

            var entity = await _context.ViewNetworks
                .Where(n => n.Id == id && n.ViewId == viewId)
                .SingleOrDefaultAsync(ct);

            if (entity == null)
                throw new EntityNotFoundException<ViewNetwork>();

            _context.ViewNetworks.Remove(entity);
            await _context.SaveChangesAsync(ct);
        }

        public async Task<ViewNetworkDto> UpdateViewNetworkTeams(Guid viewId, Guid id, UpdateViewNetworkTeamsForm form, CancellationToken ct)
        {
            if (!await CanManageView(viewId, ct))
                throw new ForbiddenException();

            var entity = await _context.ViewNetworks
                .Where(n => n.Id == id && n.ViewId == viewId)
                .SingleOrDefaultAsync(ct);

            if (entity == null)
                throw new EntityNotFoundException<ViewNetwork>();

            entity.TeamIds = form.TeamIds ?? [];
            await _context.SaveChangesAsync(ct);

            return MapToDto(entity);
        }

        public async Task<EffectiveNetworkPermission> GetEffectiveNetworkPermissions(
            Guid viewId, IEnumerable<Guid> vmTeamIds, string[] vmAllowedNetworks,
            VmType providerType, string providerInstanceId,
            CancellationToken ct)
        {
            if (await _playerService.HasFullNetworkAccess(vmTeamIds, ct))
            {
                // Full access is now scoped to view-registered networks
                var viewNetworkIds = await _context.ViewNetworks
                    .Where(n => n.ViewId == viewId
                        && n.ProviderType == providerType
                        && n.ProviderInstanceId == providerInstanceId)
                    .Select(n => n.NetworkId)
                    .ToArrayAsync(ct);

                return new EffectiveNetworkPermission
                {
                    HasFullAccess = false,
                    AllowedNetworkIds = viewNetworkIds
                };
            }

            var userTeamIds = (await _playerService.GetUserTeamIds(vmTeamIds, ct)).ToArray();

            var allowedNetworkIds = await _context.ViewNetworks
                .Where(n => n.ViewId == viewId
                    && n.ProviderType == providerType
                    && n.ProviderInstanceId == providerInstanceId
                    && n.TeamIds.Any(t => userTeamIds.Contains(t)))
                .Select(n => n.NetworkId)
                .Distinct()
                .ToArrayAsync(ct);

            if (allowedNetworkIds.Length > 0)
            {
                return new EffectiveNetworkPermission
                {
                    HasFullAccess = false,
                    AllowedNetworkIds = allowedNetworkIds
                };
            }

            // Fallback to legacy VM-level AllowedNetworks
            return new EffectiveNetworkPermission
            {
                HasFullAccess = false,
                AllowedNetworkIds = vmAllowedNetworks
            };
        }

        private Task<bool> CanManageView(Guid viewId, CancellationToken ct)
        {
            return _playerService.Can(
                [], [viewId],
                [AppSystemPermission.ManageViews],
                [AppViewPermission.ManageView],
                [],
                ct);
        }

        private static ViewNetworkDto MapToDto(ViewNetwork entity)
        {
            return new ViewNetworkDto
            {
                Id = entity.Id,
                ViewId = entity.ViewId,
                ProviderType = entity.ProviderType,
                ProviderInstanceId = entity.ProviderInstanceId,
                NetworkId = entity.NetworkId,
                TeamIds = entity.TeamIds ?? []
            };
        }
    }
}
