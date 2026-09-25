// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Crucible.Common.EntityEvents.Events;
using MediatR;
using Player.Vm.Api.Domain.Services;

namespace Player.Vm.Api.Features.Vms.EventHandlers;

public sealed class VmCreatedInitializationHandler(IVmInitializationQueue queue)
    : INotificationHandler<EntityCreated<Domain.Models.Vm>>
{
    public Task Handle(EntityCreated<Domain.Models.Vm> notification, CancellationToken cancellationToken)
    {
        queue.Enqueue(notification.Entity);
        return Task.CompletedTask;
    }
}
