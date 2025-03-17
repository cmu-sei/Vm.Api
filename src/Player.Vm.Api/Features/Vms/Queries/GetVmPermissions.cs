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

    public class Handler(VmContext db, IViewService viewService, IPlayerApiClient playerApiClient) : IRequestHandler<Query, VmPermissionResult>
    {
        public async Task<VmPermissionResult> Handle(Query request, CancellationToken cancellationToken)
        {
            var teamIds = await db.VmTeams
                .Where(x => x.VmId == request.Id)
                .Select(x => x.TeamId)
                .ToArrayAsync(cancellationToken);

            var viewIds = await viewService.GetViewIdsForTeams(teamIds, cancellationToken);

            var tasks = new List<Task<ICollection<TeamPermissionsClaim>>>();

            foreach (var viewId in viewIds)
            {
                tasks.Add(playerApiClient.GetMyTeamPermissionsAsync(viewId, null, true));
            }

            await Task.WhenAll(tasks);

            var primaryPermissions = tasks
                .SelectMany(x => x.Result.Where(y => y.IsPrimary))
                .SelectMany(x => x.PermissionValues);

            var appViewPermissions = primaryPermissions
                .Select(x => Enum.TryParse<AppViewPermission>(x, out var p) ? p : (AppViewPermission?)null)
                .Where(p => p.HasValue)
                .Select(p => p.Value);

            var appTeamPermissions = primaryPermissions
                .Select(x => Enum.TryParse<AppTeamPermission>(x, out var p) ? p : (AppTeamPermission?)null)
                .Where(p => p.HasValue)
                .Select(p => p.Value);

            return new VmPermissionResult
            {
                TeamPermissions = appTeamPermissions.ToArray(),
                ViewPermissions = appViewPermissions.ToArray()
            };
        }
    }
}