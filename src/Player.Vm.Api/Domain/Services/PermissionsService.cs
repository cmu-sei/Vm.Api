// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using System.Collections.Concurrent;
using Player.Api.Client;

namespace Player.Vm.Api.Domain.Services
{
    public interface IPermissionsService
    {
        Task<IEnumerable<Permissions>> GetViewPermissions(Guid viewId, CancellationToken cancellationToken);
        Task<IEnumerable<Permissions>> GetPermissions(IEnumerable<Guid> teamIds, CancellationToken cancellationToken);
        Task<bool> CanWrite(IEnumerable<Guid> teamIds, CancellationToken cancellationToken);
    }

    public class PermissionsService : IPermissionsService
    {
        private readonly IViewService _viewService;
        private readonly IPlayerService _playerService;
        private readonly ConcurrentDictionary<Guid, IEnumerable<Permission>> _viewPermissionsDict = new ConcurrentDictionary<Guid, IEnumerable<Permission>>();

        public PermissionsService(
            IViewService viewService,
            IPlayerService playerService)
        {
            _viewService = viewService;
            _playerService = playerService;
        }

        public async Task<IEnumerable<Permissions>> GetViewPermissions(Guid viewId, CancellationToken cancellationToken)
        {
            var permissions = new List<Permissions>();
            var viewPermissions = await _playerService.GetPermissionsByViewIdAsync(viewId, cancellationToken);

            if (viewPermissions.Any(x => x.Key.ToLower() == Permissions.ReadOnly.ToString().ToLower() && x.Value.ToLower() == "true"))
            {
                permissions.Add(Permissions.ReadOnly);
            }

            return permissions;
        }

        public async Task<IEnumerable<Permissions>> GetPermissions(IEnumerable<Guid> teamIds, CancellationToken cancellationToken)
        {
            var vmPermissions = new List<Permissions>();
            var viewPermissions = new List<Permission>();
            var viewIds = await _viewService.GetViewIdsForTeams(teamIds, cancellationToken);
            var taskDict = new Dictionary<Guid, Task<IEnumerable<Permission>>>();

            foreach (var viewId in viewIds)
            {
                if (_viewPermissionsDict.TryGetValue(viewId, out IEnumerable<Permission> cachedPermissions))
                {
                    viewPermissions.AddRange(cachedPermissions);
                }
                else
                {
                    var permissionsTask = _playerService.GetPermissionsByViewIdAsync(viewId, cancellationToken);
                    taskDict.Add(viewId, permissionsTask);
                }
            }

            await Task.WhenAll(taskDict.Values);

            foreach (var kvp in taskDict)
            {
                var viewId = kvp.Key;
                var permissions = kvp.Value.Result;

                viewPermissions.AddRange(permissions);
                _viewPermissionsDict.AddOrUpdate(viewId, permissions, (viewId, p) =>
                {
                    return permissions;
                });
            }

            if (viewPermissions.Any(x => x.Key.ToLower() == Permissions.ReadOnly.ToString().ToLower() && x.Value.ToLower() == "true"))
            {
                vmPermissions.Add(Permissions.ReadOnly);
            }

            return vmPermissions;
        }

        public async Task<bool> CanWrite(IEnumerable<Guid> teamIds, CancellationToken cancellationToken)
        {
            var permissionList = await this.GetPermissions(teamIds, cancellationToken);

            if (permissionList.Contains(Permissions.ReadOnly))
            {
                return false;
            }
            else
            {
                return true;
            }
        }
    }
}
