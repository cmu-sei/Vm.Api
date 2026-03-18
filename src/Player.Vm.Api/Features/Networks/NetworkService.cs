// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
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
        Task<ViewNetwork[]> GetByViewId(Guid viewId, CancellationToken ct);
        Task<ViewNetwork> GetById(Guid viewId, Guid id, CancellationToken ct);
        Task<ViewNetwork> CreateViewNetwork(Guid viewId, CreateViewNetworkForm form, CancellationToken ct);
        Task<ViewNetwork> UpdateViewNetwork(Guid viewId, Guid id, UpdateViewNetworkForm form, CancellationToken ct);
        Task DeleteViewNetwork(Guid viewId, Guid id, CancellationToken ct);
        Task<EffectiveNetworkPermission> GetEffectiveNetworkPermissions(
            Guid viewId, IEnumerable<Guid> vmTeamIds,
            VmType providerType, string providerInstanceId,
            CancellationToken ct);
    }

    public class NetworkService : INetworkService
    {
        private readonly VmContext _context;
        private readonly IPlayerService _playerService;
        private readonly IMapper _mapper;

        public NetworkService(VmContext context, IPlayerService playerService, IMapper mapper)
        {
            _context = context;
            _playerService = playerService;
            _mapper = mapper;
        }

        public async Task<ViewNetwork[]> GetByViewId(Guid viewId, CancellationToken ct)
        {
            if (!await CanViewNetworks(viewId, ct))
                throw new ForbiddenException();

            var entities = await _context.ViewNetworks
                .Where(n => n.ViewId == viewId)
                .ToArrayAsync(ct);

            return _mapper.Map<ViewNetwork[]>(entities);
        }

        public async Task<ViewNetwork> CreateViewNetwork(Guid viewId, CreateViewNetworkForm form, CancellationToken ct)
        {
            if (!await CanManageNetworks(viewId, ct))
                throw new ForbiddenException();

            var existing = await _context.ViewNetworks
                .Where(n => n.ViewId == viewId
                    && n.ProviderType == form.ProviderType
                    && n.ProviderInstanceId == form.ProviderInstanceId
                    && n.NetworkId == form.NetworkId)
                .SingleOrDefaultAsync(ct);

            if (existing != null)
                return _mapper.Map<ViewNetwork>(existing);

            var entity = _mapper.Map<Domain.Models.ViewNetwork>(form);
            entity.ViewId = viewId;

            _context.ViewNetworks.Add(entity);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<ViewNetwork>(entity);
        }

        public async Task DeleteViewNetwork(Guid viewId, Guid id, CancellationToken ct)
        {
            if (!await CanManageNetworks(viewId, ct))
                throw new ForbiddenException();

            var entity = await _context.ViewNetworks
                .Where(n => n.Id == id && n.ViewId == viewId)
                .SingleOrDefaultAsync(ct);

            if (entity == null)
                throw new EntityNotFoundException<ViewNetwork>();

            _context.ViewNetworks.Remove(entity);
            await _context.SaveChangesAsync(ct);
        }

        public async Task<ViewNetwork> GetById(Guid viewId, Guid id, CancellationToken ct)
        {
            if (!await CanViewNetworks(viewId, ct))
                throw new ForbiddenException();

            var entity = await _context.ViewNetworks
                .Where(n => n.Id == id && n.ViewId == viewId)
                .SingleOrDefaultAsync(ct);

            if (entity == null)
                throw new EntityNotFoundException<ViewNetwork>();

            return _mapper.Map<ViewNetwork>(entity);
        }

        public async Task<ViewNetwork> UpdateViewNetwork(Guid viewId, Guid id, UpdateViewNetworkForm form, CancellationToken ct)
        {
            if (!await CanManageNetworks(viewId, ct))
                throw new ForbiddenException();

            var entity = await _context.ViewNetworks
                .Where(n => n.Id == id && n.ViewId == viewId)
                .SingleOrDefaultAsync(ct);

            if (entity == null)
                throw new EntityNotFoundException<ViewNetwork>();

            if (entity.ProviderType != form.ProviderType
                || entity.ProviderInstanceId != form.ProviderInstanceId
                || entity.NetworkId != form.NetworkId)
            {
                var conflict = await _context.ViewNetworks
                    .Where(n => n.ViewId == viewId
                        && n.ProviderType == form.ProviderType
                        && n.ProviderInstanceId == form.ProviderInstanceId
                        && n.NetworkId == form.NetworkId
                        && n.Id != id)
                    .AnyAsync(ct);

                if (conflict)
                    throw new BadRequestException(
                        "A network with the same ProviderType, ProviderInstanceId, and NetworkId already exists for this view.");
            }

            _mapper.Map(form, entity);

            await _context.SaveChangesAsync(ct);

            return _mapper.Map<ViewNetwork>(entity);
        }

        public async Task<EffectiveNetworkPermission> GetEffectiveNetworkPermissions(
            Guid viewId, IEnumerable<Guid> vmTeamIds,
            VmType providerType, string providerInstanceId,
            CancellationToken ct)
        {
            if (await _playerService.HasViewNetworkAccess(vmTeamIds, ct))
            {
                var viewNetworks = await _context.ViewNetworks
                    .Where(n => n.ViewId == viewId
                        && n.ProviderType == providerType
                        && n.ProviderInstanceId == providerInstanceId)
                    .ToDictionaryAsync(n => n.NetworkId, n => n.Name ?? "", ct);

                return new EffectiveNetworkPermission { AllowedNetworks = viewNetworks };
            }

            var userTeamIds = (await _playerService.GetUserTeamIds(vmTeamIds, ct)).ToArray();

            var teamViewNetworks = await _context.ViewNetworks
                .Where(n => n.ViewId == viewId
                    && n.ProviderType == providerType
                    && n.ProviderInstanceId == providerInstanceId
                    && n.TeamIds.Any(t => userTeamIds.Contains(t)))
                .ToDictionaryAsync(n => n.NetworkId, n => n.Name ?? "", ct);

            return new EffectiveNetworkPermission { AllowedNetworks = teamViewNetworks };
        }

        private Task<bool> CanViewNetworks(Guid viewId, CancellationToken ct)
        {
            return _playerService.Can(
                [], [viewId],
                [AppSystemPermission.ViewNetworks, AppSystemPermission.ManageNetworks],
                [AppViewPermission.ViewNetworks, AppViewPermission.ManageNetworks],
                [],
                ct);
        }

        private Task<bool> CanManageNetworks(Guid viewId, CancellationToken ct)
        {
            return _playerService.Can(
                [], [viewId],
                [AppSystemPermission.ManageNetworks],
                [AppViewPermission.ManageNetworks],
                [],
                ct);
        }

    }
}
