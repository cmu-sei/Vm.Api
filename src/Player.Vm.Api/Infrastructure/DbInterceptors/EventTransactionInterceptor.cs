// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Events;

namespace Player.Vm.Api.Infrastructure.DbInterceptors
{
    public class EventTransactionInterceptor : DbTransactionInterceptor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<EventTransactionInterceptor> _logger;

        public EventTransactionInterceptor(
            IServiceProvider serviceProvider,
            ILogger<EventTransactionInterceptor> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                await PublishEvents(eventData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in EventTransactionInterceptor");
            }
            finally
            {
                await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
            }
        }

        public override async void TransactionCommitted(
            DbTransaction transaction,
            TransactionEndEventData eventData)
        {
            try
            {
                await PublishEvents(eventData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in EventTransactionInterceptor");
            }
            finally
            {
                base.TransactionCommitted(transaction, eventData);
            }
        }

        private async Task PublishEvents(TransactionEndEventData eventData)
        {
            var entries = GetEntries(eventData.Context.ChangeTracker);

            using (var scope = _serviceProvider.CreateScope())
            {
                var events = new List<INotification>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                foreach (var entry in entries)
                {
                    var entityType = entry.Entity.GetType();
                    Type eventType = null;

                    string[] modifiedProperties = null;

                    switch (entry.State)
                    {
                        case EntityState.Added:
                            eventType = typeof(EntityCreated<>).MakeGenericType(entityType);
                            break;
                        case EntityState.Modified:
                            eventType = typeof(EntityUpdated<>).MakeGenericType(entityType);
                            modifiedProperties = entry.Properties
                                .Where(x => x.IsModified)
                                .Select(x => x.Metadata.Name)
                                .ToArray();
                            break;
                        case EntityState.Deleted:
                            eventType = typeof(EntityDeleted<>).MakeGenericType(entityType);
                            break;
                    }

                    if (eventType != null)
                    {
                        INotification evt;

                        if (modifiedProperties != null)
                        {
                            evt = Activator.CreateInstance(eventType, new[] { entry.Entity, modifiedProperties }) as INotification;
                        }
                        else
                        {
                            evt = Activator.CreateInstance(eventType, new[] { entry.Entity }) as INotification;
                        }


                        if (evt != null)
                        {
                            events.Add(evt);
                        }
                    }
                }

                foreach (var evt in events)
                {
                    await mediator.Publish(evt);
                }
            }
        }

        private EntityEntry[] GetEntries(ChangeTracker changeTracker)
        {
            var entries = changeTracker.Entries()
                .Where(x => x.State == EntityState.Added ||
                            x.State == EntityState.Modified ||
                            x.State == EntityState.Deleted)
                .ToList();

            // Remove children so we don't duplicate events
            foreach (var entry in entries.ToArray())
            {
                foreach (var collection in entry.Collections)
                {
                    foreach (var val in collection.CurrentValue)
                    {
                        var e = entries.Where(e => e.Entity == val).FirstOrDefault();

                        if (e != null)
                        {
                            entries.Remove(e);
                        }
                    }
                }
            }

            return entries.ToArray();
        }
    }
}