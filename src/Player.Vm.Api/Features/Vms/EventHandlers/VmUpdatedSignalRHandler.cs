// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
using Crucible.Common.EntityEvents.Events;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Infrastructure.Extensions;

namespace Player.Vm.Api.Features.Vms.EventHandlers
{
    public class VmBaseSignalRHandler
    {
        protected readonly VmContext _db;
        protected readonly IMapper _mapper;
        protected readonly IViewService _viewService;
        protected readonly IHubContext<VmHub> _vmHub;

        public VmBaseSignalRHandler(
            VmContext db,
            IMapper mapper,
            IViewService viewService,
            IHubContext<VmHub> vmHub)
        {
            _db = db;
            _mapper = mapper;
            _viewService = viewService;
            _vmHub = vmHub;
        }

        protected async Task<Guid[]> GetGroups(Domain.Models.Vm vm, CancellationToken cancellationToken)
        {
            var groupIds = new List<Guid>();

            foreach (var teamId in vm.VmTeams.Select(x => x.TeamId))
            {
                var viewId = await _viewService.GetViewIdForTeam(teamId, cancellationToken);

                if (viewId.HasValue && !groupIds.Any(v => v == viewId.Value))
                {
                    groupIds.Add(viewId.Value);
                }

                groupIds.Add(teamId);
            }

            return groupIds.ToArray();
        }

        protected async Task HandleCreateOrUpdate(
            Domain.Models.Vm vmEntity,
            string method,
            string[] modifiedProperties,
            CancellationToken cancellationToken)
        {
            if (!vmEntity.TeamsLoaded)
            {
                if (_db.Entry(vmEntity).State == EntityState.Detached)
                {
                    _db.Attach(vmEntity);
                }

                await _db.Entry(vmEntity)
                    .Collection(x => x.VmTeams)
                    .LoadAsync(cancellationToken);
            }

            var groupIds = await this.GetGroups(vmEntity, cancellationToken);

            // The last await before the sends, so nothing announced after this read can reach a client first.
            var vm = _mapper.Map<Vm>(await ToAnnounce(vmEntity, cancellationToken));
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_vmHub.Clients.Group(groupId.ToString()).SendAsync(method, vm, modifiedProperties, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>The Vm whose state is sent, once its teams are loaded.</summary>
        protected virtual Task<Domain.Models.Vm> ToAnnounce(Domain.Models.Vm vmEntity, CancellationToken cancellationToken) =>
            Task.FromResult(vmEntity);
    }

    public class VmCreatedSignalRHandler : VmBaseSignalRHandler, INotificationHandler<EntityCreated<Domain.Models.Vm>>
    {
        public VmCreatedSignalRHandler(
            VmContext db,
            IMapper mapper,
            IViewService viewService,
            IHubContext<VmHub> vmHub) : base(db, mapper, viewService, vmHub) { }

        public async Task Handle(EntityCreated<Domain.Models.Vm> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(notification.Entity, VmHubMethods.VmCreated, null, cancellationToken);
        }

        // Handlers run in no guaranteed order, so another handler may already have written state to the new row
        // (the vSphere persister does) and announced it as an update. Sending the event's copy would then overwrite
        // that on the client, so the create sends the committed row instead. Untracked, because this context holds
        // the event's copy and a tracked query would return it. A row already gone keeps the event's values.
        protected override async Task<Domain.Models.Vm> ToAnnounce(Domain.Models.Vm vmEntity, CancellationToken cancellationToken)
        {
            var committed = await _db.Vms
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == vmEntity.Id, cancellationToken);

            if (committed == null)
                return vmEntity;

            committed.VmTeams = vmEntity.VmTeams;
            return committed;
        }
    }

    public class VmUpdatedSignalRHandler : VmBaseSignalRHandler, INotificationHandler<EntityUpdated<Domain.Models.Vm>>
    {
        public VmUpdatedSignalRHandler(
            VmContext db,
            IMapper mapper,
            IViewService viewService,
            IHubContext<VmHub> vmHub) : base(db, mapper, viewService, vmHub) { }

        public async Task Handle(EntityUpdated<Domain.Models.Vm> notification, CancellationToken cancellationToken)
        {
            await base.HandleCreateOrUpdate(
                notification.Entity,
                VmHubMethods.VmUpdated,
                notification.ModifiedProperties.Select(x => x.TitleCaseToCamelCase()).ToArray(),
                cancellationToken);
        }
    }

    public class VmDeletedSignalRHandler : VmBaseSignalRHandler, INotificationHandler<EntityDeleted<Domain.Models.Vm>>
    {
        public VmDeletedSignalRHandler(
            VmContext db,
            IMapper mapper,
            IViewService viewService,
            IHubContext<VmHub> vmHub) : base(db, mapper, viewService, vmHub)
        {
        }

        public async Task Handle(EntityDeleted<Domain.Models.Vm> notification, CancellationToken cancellationToken)
        {
            var groupIds = await base.GetGroups(notification.Entity, cancellationToken);
            var tasks = new List<Task>();

            foreach (var groupId in groupIds)
            {
                tasks.Add(_vmHub.Clients.Group(groupId.ToString()).SendAsync(VmHubMethods.VmDeleted, notification.Entity.Id, cancellationToken));
            }

            // If a vm was deleted without loading it's teams, just send the notification to everyone.
            // It's only an id, so no data leakage.
            if (!groupIds.Any())
            {
                tasks.Add(_vmHub.Clients.All.SendAsync(VmHubMethods.VmDeleted, notification.Entity.Id, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
