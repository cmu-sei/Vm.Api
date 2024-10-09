// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Events;

namespace Player.Vm.Api.Infrastructure.DbInterceptors;

/// <summary>
/// Intercepts saves to the database and generate Entity events from them.
///
/// As of EF7, transactions are not always created by SaveChanges for performance reasons, so we have to
/// handle both TransactionCommitted and SavedChanges. If a transaction is in progress,
/// SavedChanges will not generate the events and it will instead happen in TransactionCommitted.
/// </summary>
public class EventInterceptor : DbTransactionInterceptor, ISaveChangesInterceptor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EventInterceptor> _logger;

    private List<Entry> Entries { get; set; } = new List<Entry>();

    public EventInterceptor(
        IServiceProvider serviceProvider,
        ILogger<EventInterceptor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await TransactionCommittedInternal(eventData);
        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        TransactionCommittedInternal(eventData).Wait();
        base.TransactionCommitted(transaction, eventData);
    }

    private async Task TransactionCommittedInternal(TransactionEndEventData eventData)
    {
        try
        {
            await PublishEvents(eventData.Context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TransactionCommitted");
        }
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        SavedChangesInternal(eventData, false).Wait();
        return result;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await SavedChangesInternal(eventData, true);
        return result;
    }

    private async Task SavedChangesInternal(SaveChangesCompletedEventData eventData, bool async)
    {
        try
        {
            if (eventData.Context.Database.CurrentTransaction == null)
            {
                if (async)
                {
                    await PublishEvents(eventData.Context);
                }
                else
                {
                    PublishEvents(eventData.Context).Wait();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SavedChanges");
        }
    }

    /// <summary>
    /// Called before SaveChanges is performed. This saves the changed Entities to be used at the end of the
    /// transaction for creating events from the final set of changes. May be called multiple times for a single
    /// transaction.
    /// </summary>
    /// <returns></returns>
    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        SaveEntries(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default(CancellationToken))
    {
        SaveEntries(eventData.Context);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    /// <summary>
    /// Creates and publishes events from the current set of entity changes.
    /// </summary>
    /// <param name="dbContext">The DbContext used for this transaction</param>
    /// <returns></returns>
    private async Task PublishEvents(DbContext dbContext)
    {
        IServiceScope scope = null;

        try
        {
            // Try to get required services from the current scope that has been injected into the
            // dbContext from the ContextFactory. This allows us to use the same scope in the event handlers.
            // If no ServiceProvider exists on the context, create a new scope with the root ServiceProvider.
            IMediator mediator = null;

            if (dbContext is VmContext)
            {
                var context = dbContext as VmContext;
                if (context.ServiceProvider != null)
                {
                    mediator = context.ServiceProvider.GetRequiredService<IMediator>();
                }
            }

            if (mediator == null)
            {
                scope = _serviceProvider.CreateScope();
                mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            }

            await PublishEventsInternal(mediator);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in PublishEvents");
        }
        finally
        {
            if (scope != null)
            {
                scope.Dispose();
            }
        }
    }

    private async Task PublishEventsInternal(IMediator mediator)
    {
        var events = new List<INotification>();
        var entries = GetEntries();

        foreach (var entry in entries)
        {
            var entityType = entry.Entity.GetType();
            Type eventType = null;

            string[] modifiedProperties = null;

            switch (entry.State)
            {
                case EntityState.Added:
                    eventType = typeof(EntityCreated<>).MakeGenericType(entityType);

                    // Make sure properties generated by the db are set
                    var generatedProps = entry.Properties
                        .Where(x => x.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd)
                        .ToList();

                    foreach (var prop in generatedProps)
                    {
                        entityType.GetProperty(prop.Metadata.Name).SetValue(entry.Entity, prop.CurrentValue);
                    }

                    break;
                case EntityState.Modified:
                    eventType = typeof(EntityUpdated<>).MakeGenericType(entityType);
                    modifiedProperties = entry.GetModifiedProperties();
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

    private Entry[] GetEntries()
    {
        var entries = Entries
            .Where(x => x.State == EntityState.Added ||
                        x.State == EntityState.Modified ||
                        x.State == EntityState.Deleted)
            .ToList();

        Entries.Clear();
        return entries.ToArray();
    }

    /// <summary>
    /// Keeps track of changes across multiple savechanges in a transaction, without duplicates
    /// </summary>
    private void SaveEntries(DbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries())
        {
            // find value of id property
            var id = entry.Properties
                .FirstOrDefault(x =>
                    x.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd)?.CurrentValue;

            // find matching existing entry, if any
            Entry e = null;

            if (id != null)
            {
                e = Entries.FirstOrDefault(x => id.Equals(x.Properties.FirstOrDefault(y =>
                    y.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd)?.CurrentValue));
            }

            if (e != null)
            {
                // if entry already exists, mark which properties were previously modified,
                // remove old entry and add new one, to avoid duplicates
                var newEntry = new Entry(entry, e);
                Entries.Remove(e);
                Entries.Add(newEntry);
            }
            else
            {
                Entries.Add(new Entry(entry));
            }
        }
    }
}