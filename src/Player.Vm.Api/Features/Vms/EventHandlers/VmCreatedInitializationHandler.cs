// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Crucible.Common.EntityEvents.Events;
using MediatR;
using Player.Vm.Api.Domain.Vsphere.Services;

namespace Player.Vm.Api.Features.Vms.EventHandlers;

/// <summary>
/// Once a new Vm row has committed, queues it for the vSphere persister. The row starts Unknown; if the
/// watcher has already reported the machine, the persister writes its cached state straight after. The
/// watcher alone would miss a machine it reported before the row existed. No vCenter call.
/// </summary>
public sealed class VmCreatedInitializationHandler(IConnectionService connections)
    : INotificationHandler<EntityCreated<Domain.Models.Vm>>
{
    public Task Handle(EntityCreated<Domain.Models.Vm> notification, CancellationToken cancellationToken)
    {
        connections.MarkDirty(notification.Entity.Id);
        return Task.CompletedTask;
    }
}
