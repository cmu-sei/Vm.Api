// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Events;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms.Hubs;

namespace Player.Vm.Api.Features.Vms.EventHandlers
{
    public class VmTeamCreatedSignalRHandler : VmBaseSignalRHandler, INotificationHandler<EntityCreated<Domain.Models.VmTeam>>
    {
        public VmTeamCreatedSignalRHandler(
            VmContext db,
            IMapper mapper,
            IViewService viewService,
            IHubContext<VmHub> vmHub) : base(db, mapper, viewService, vmHub) { }

        public async Task Handle(EntityCreated<Domain.Models.VmTeam> notification, CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();

            if (notification.Entity.Vm == null)
            {
                if (_db.Entry(notification.Entity).State == EntityState.Detached)
                {
                    _db.Attach(notification.Entity);
                }

                notification.Entity.Vm = await _db.Vms
                    .Where(x => x.Id == notification.Entity.VmId)
                    .Include(x => x.VmTeams)
                    .FirstOrDefaultAsync();
            }

            if (notification.Entity.Vm == null)
            {
                return;
            }

            var vm = _mapper.Map<Vm>(notification.Entity.Vm);
            var viewId = await _viewService.GetViewIdForTeam(notification.Entity.TeamId, cancellationToken);

            if (viewId.HasValue)
            {
                foreach (var teamId in notification.Entity.Vm.VmTeams.Select(x => x.TeamId))
                {
                    if (teamId != notification.Entity.TeamId)
                    {
                        var vId = await _viewService.GetViewIdForTeam(teamId, cancellationToken);

                        // if this vm was already on a team in the same view, don't notify that view again
                        if (vId.HasValue && vId.Value == viewId.Value)
                        {
                            viewId = null;
                            break;
                        }
                    }
                }

                if (viewId.HasValue)
                {
                    tasks.Add(_vmHub.Clients.Group(viewId.ToString()).SendAsync(VmHubMethods.VmCreated, vm, cancellationToken));
                }
            }

            tasks.Add(_vmHub.Clients.Group(notification.Entity.TeamId.ToString()).SendAsync(VmHubMethods.VmCreated, vm, cancellationToken));
            await Task.WhenAll(tasks);
        }
    }

    public class VmTeamDeletedSignalRHandler : VmBaseSignalRHandler, INotificationHandler<EntityDeleted<Domain.Models.VmTeam>>
    {
        public VmTeamDeletedSignalRHandler(
            VmContext db,
            IMapper mapper,
            IViewService viewService,
            IHubContext<VmHub> vmHub) : base(db, mapper, viewService, vmHub) { }

        public async Task Handle(EntityDeleted<Domain.Models.VmTeam> notification, CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();

            if (notification.Entity.Vm == null)
            {
                if (_db.Entry(notification.Entity).State == EntityState.Detached)
                {
                    _db.Attach(notification.Entity);
                }

                notification.Entity.Vm = await _db.Vms
                    .Where(x => x.Id == notification.Entity.VmId)
                    .Include(x => x.VmTeams)
                    .FirstOrDefaultAsync();
            }

            if (notification.Entity.Vm != null)
            {
                var vm = _mapper.Map<Vm>(notification.Entity.Vm);
                var viewId = await _viewService.GetViewIdForTeam(notification.Entity.TeamId, cancellationToken);

                if (viewId.HasValue)
                {
                    foreach (var teamId in notification.Entity.Vm.VmTeams.Select(x => x.TeamId))
                    {
                        if (teamId != notification.Entity.TeamId)
                        {
                            var vId = await _viewService.GetViewIdForTeam(teamId, cancellationToken);

                            // if this vm is still on a team in the same view, don't notify that view
                            if (vId.HasValue && vId.Value == viewId.Value)
                            {
                                viewId = null;
                                break;
                            }
                        }
                    }

                    if (viewId.HasValue)
                    {
                        tasks.Add(_vmHub.Clients.Group(viewId.ToString()).SendAsync(VmHubMethods.VmDeleted, notification.Entity.VmId, cancellationToken));
                    }
                }
            }

            tasks.Add(_vmHub.Clients.Group(notification.Entity.TeamId.ToString()).SendAsync(VmHubMethods.VmDeleted, notification.Entity.VmId, cancellationToken));
            await Task.WhenAll(tasks);
        }
    }
}
