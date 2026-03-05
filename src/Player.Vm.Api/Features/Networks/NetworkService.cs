// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
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
        Task<TeamNetworkPermission[]> GetByTeamId(Guid teamId, CancellationToken ct);
        Task<TeamNetworkPermission> Get(Guid teamId, Guid id, CancellationToken ct);
        Task<TeamNetworkPermission> Create(Guid teamId, TeamNetworkPermissionForm form, CancellationToken ct);
        Task Delete(Guid teamId, Guid id, CancellationToken ct);
        Task DeleteAllByTeam(Guid teamId, CancellationToken ct);
        Task<EffectiveNetworkPermission> GetEffectiveNetworkPermissions(
            IEnumerable<Guid> vmTeamIds, string[] vmAllowedNetworks, CancellationToken ct);
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

        public async Task<TeamNetworkPermission[]> GetByTeamId(Guid teamId, CancellationToken ct)
        {
            if (!await _playerService.CanManageTeams([teamId], ct))
                throw new ForbiddenException();

            var entities = await _context.TeamNetworkPermissions
                .Where(t => t.TeamId == teamId)
                .ToArrayAsync(ct);

            return entities.Select(MapToDto).ToArray();
        }

        public async Task<TeamNetworkPermission> Get(Guid teamId, Guid id, CancellationToken ct)
        {
            if (!await _playerService.CanManageTeams([teamId], ct))
                throw new ForbiddenException();

            var entity = await _context.TeamNetworkPermissions
                .Where(t => t.Id == id && t.TeamId == teamId)
                .SingleOrDefaultAsync(ct);

            if (entity == null)
                throw new EntityNotFoundException<TeamNetworkPermission>();

            return MapToDto(entity);
        }

        public async Task<TeamNetworkPermission> Create(Guid teamId, TeamNetworkPermissionForm form, CancellationToken ct)
        {
            if (!await _playerService.CanManageTeams([teamId], ct))
                throw new ForbiddenException();

            var type = form.Type ?? "";
            var externalId = form.ExternalId ?? "";

            // Idempotent: return existing if duplicate
            var existing = await _context.TeamNetworkPermissions
                .Where(t => t.TeamId == teamId
                    && t.NetworkName == form.NetworkName
                    && t.Type == type
                    && t.ExternalId == externalId)
                .SingleOrDefaultAsync(ct);

            if (existing != null)
                return MapToDto(existing);

            var entity = new Domain.Models.TeamNetworkPermission
            {
                TeamId = teamId,
                NetworkName = form.NetworkName,
                Type = type,
                ExternalId = externalId
            };

            _context.TeamNetworkPermissions.Add(entity);
            await _context.SaveChangesAsync(ct);

            return MapToDto(entity);
        }

        public async Task Delete(Guid teamId, Guid id, CancellationToken ct)
        {
            if (!await _playerService.CanManageTeams([teamId], ct))
                throw new ForbiddenException();

            var entity = await _context.TeamNetworkPermissions
                .Where(t => t.Id == id && t.TeamId == teamId)
                .SingleOrDefaultAsync(ct);

            if (entity == null)
                throw new EntityNotFoundException<TeamNetworkPermission>();

            _context.TeamNetworkPermissions.Remove(entity);
            await _context.SaveChangesAsync(ct);
        }

        public async Task DeleteAllByTeam(Guid teamId, CancellationToken ct)
        {
            if (!await _playerService.CanManageTeams([teamId], ct))
                throw new ForbiddenException();

            var entities = await _context.TeamNetworkPermissions
                .Where(t => t.TeamId == teamId)
                .ToListAsync(ct);

            _context.TeamNetworkPermissions.RemoveRange(entities);
            await _context.SaveChangesAsync(ct);
        }

        public async Task<EffectiveNetworkPermission> GetEffectiveNetworkPermissions(
            IEnumerable<Guid> vmTeamIds, string[] vmAllowedNetworks, CancellationToken ct)
        {
            if (await _playerService.HasFullNetworkAccess(vmTeamIds, ct))
            {
                return new EffectiveNetworkPermission { HasFullAccess = true };
            }

            var userTeamIds = await _playerService.GetUserTeamIds(vmTeamIds, ct);

            var allowedNetworks = await _context.TeamNetworkPermissions
                .Where(t => userTeamIds.Contains(t.TeamId))
                .Select(t => t.NetworkName)
                .Distinct()
                .ToArrayAsync(ct);

            if (allowedNetworks.Length > 0)
            {
                return new EffectiveNetworkPermission
                {
                    HasFullAccess = false,
                    AllowedNetworks = allowedNetworks
                };
            }

            // Fallback to legacy VM-level AllowedNetworks
            return new EffectiveNetworkPermission
            {
                HasFullAccess = false,
                AllowedNetworks = vmAllowedNetworks
            };
        }

        private static TeamNetworkPermission MapToDto(Domain.Models.TeamNetworkPermission entity)
        {
            return new TeamNetworkPermission
            {
                Id = entity.Id,
                TeamId = entity.TeamId,
                NetworkName = entity.NetworkName,
                Type = entity.Type,
                ExternalId = entity.ExternalId
            };
        }
    }
}
