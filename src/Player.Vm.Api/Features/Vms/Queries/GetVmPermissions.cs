// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
using Player.Api.Client;

namespace Player.Vm.Api.Features.Vms;

public class GetVmPermissions
{
    [DataContract(Name = "GetVmPermissions")]
    public class Query : IRequest<VmPermissionResult>
    {
        public Guid Id { get; set; }
    }

    public class Handler(IVmService vmService, IViewService viewService, IPlayerApiClient playerApiClient, IPlayerService playerService) : IRequestHandler<Query, VmPermissionResult>
    {
        public async Task<VmPermissionResult> Handle(Query request, CancellationToken cancellationToken)
        {
            var vm = await vmService.GetAsync(request.Id, cancellationToken);
            var teamIds = vm.TeamIds.ToArray();

            var viewIds = await viewService.GetViewIdsForTeams(teamIds, cancellationToken);

            var tasks = new List<Task<ICollection<TeamPermissionsClaim>>>();

            foreach (var viewId in viewIds)
            {
                tasks.Add(playerApiClient.GetMyTeamPermissionsAsync(viewId, null, true, cancellationToken));
            }

            await Task.WhenAll(tasks);

            var claims = tasks.SelectMany(x => x.Result);
            var targetPermissionValues = claims
                .Where(x => teamIds.Contains(x.TeamId))
                .SelectMany(x => x.PermissionValues ?? []);
            var directViewPermissionValues = claims
                .SelectMany(x => x.DirectPermissionValues ?? []);

            var appViewPermissions = AppPermissions.Parse<AppViewPermission>(
                targetPermissionValues.Concat(directViewPermissionValues));

            var appTeamPermissions = AppPermissions.Parse<AppTeamPermission>(targetPermissionValues);

            var appSystemPermissions = await playerService.GetSystemPermissionsAsync(cancellationToken);

            return new VmPermissionResult
            {
                SystemPermissions = appSystemPermissions.ToArray(),
                TeamPermissions = appTeamPermissions.ToArray(),
                ViewPermissions = appViewPermissions.ToArray()
            };
        }
    }
}
