// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private readonly ILogger<EventInterceptor> _logger;

    private List<Entry> Entries { get; set; } = new List<Entry>();
    private int _activeThreadId = 0;

    public EventInterceptor(ILogger<EventInterceptor> logger)
    {
        _logger = logger;
    }

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionCommittedInternal(eventData);
        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        TransactionCommittedInternal(eventData);
        base.TransactionCommitted(transaction, eventData);
    }

    private void TransactionCommittedInternal(TransactionEndEventData eventData)
    {
        try
        {
            // Store events in the context to be published after SaveChangesAsync completes
            // This avoids the Npgsql 10+ "Transaction is already completed" error
            SaveEvents(eventData.Context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TransactionCommitted");
        }
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        SavedChangesInternal(eventData);
        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        SavedChangesInternal(eventData);
        return new(result);
    }

    private void SavedChangesInternal(SaveChangesCompletedEventData eventData)
    {
        try
        {
            if (eventData.Context.Database.CurrentTransaction == null)
            {
                SaveEvents(eventData.Context);
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
        return new(result);
    }

    /// <summary>
    /// Creates and stores events in the context to be published after transaction cleanup
    /// </summary>
    /// <param name="dbContext">The DbContext used for this transaction</param>
    /// <returns></returns>
    private void SaveEvents(DbContext dbContext)
    {
        try
        {
            if (dbContext is VmContext context)
            {
                var events = CreateEvents();
                context.Events.AddRange(events);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SaveEvents");
        }
    }

    private List<INotification> CreateEvents()
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

        return events;
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
        var threadId = Environment.CurrentManagedThreadId;
        var previousThread = Interlocked.Exchange(ref _activeThreadId, threadId);
        if (previousThread != 0 && previousThread != threadId)
        {
            _logger.LogWarning("SaveEntries CONCURRENT ACCESS DETECTED: CurrentThread={Current}, PreviousThread={Previous}, EntriesCount={Count}",
                threadId, previousThread, Entries.Count);
        }

        _logger.LogDebug("SaveEntries START: ThreadId={ThreadId}, EntriesCount={Count}", threadId, Entries.Count);

        try
        {
        foreach (var entry in db.ChangeTracker.Entries())
        {
            try
            {
                var entityType = entry.Entity?.GetType()?.Name ?? "null";
                var entityState = entry.State;

                _logger.LogDebug("SaveEntries processing entry: ThreadId={ThreadId}, EntityType={EntityType}, State={State}", threadId, entityType, entityState);

                // find value of id property
                var idProperty = entry.Properties
                    .FirstOrDefault(x =>
                        x.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd);

                var id = idProperty?.CurrentValue;

                _logger.LogDebug("SaveEntries entry id lookup: EntityType={EntityType}, IdPropertyName={IdPropertyName}, IdValue={IdValue}",
                    entityType,
                    idProperty?.Metadata?.Name ?? "null",
                    id ?? "null");

                // find matching existing entry, if any
                Entry e = null;

                if (id != null)
                {
                    _logger.LogDebug("SaveEntries searching: ThreadId={ThreadId}, Count={Count}, Id={Id}", threadId, Entries.Count, id);

                    foreach (var existingEntry in Entries)
                    {
                        if (existingEntry == null)
                        {
                            _logger.LogWarning("SaveEntries found null entry in Entries list");
                            continue;
                        }

                        if (existingEntry.Properties == null)
                        {
                            _logger.LogWarning("SaveEntries found entry with null Properties: EntityType={EntityType}, State={State}",
                                existingEntry.Entity?.GetType()?.Name ?? "null",
                                existingEntry.State);
                            continue;
                        }

                        var existingIdProp = existingEntry.Properties.FirstOrDefault(y =>
                            y.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd);

                        if (existingIdProp != null && id.Equals(existingIdProp.CurrentValue))
                        {
                            e = existingEntry;
                            break;
                        }
                    }
                }

                if (e != null)
                {
                    _logger.LogDebug("SaveEntries updating: ThreadId={ThreadId}, EntityType={EntityType}, Id={Id}", threadId, entityType, id);

                    // if entry already exists, mark which properties were previously modified,
                    // remove old entry and add new one, to avoid duplicates
                    var newEntry = new Entry(entry, e);

                    if (newEntry.Properties == null)
                    {
                        _logger.LogWarning("SaveEntries created Entry with null Properties from update: ThreadId={ThreadId}, EntityType={EntityType}, State={State}, SourceEntryPropertiesNull={SourceNull}, OldEntryPropertiesNull={OldNull}",
                            threadId, entityType, entityState, entry.Properties == null, e.Properties == null);
                    }

                    _logger.LogDebug("SaveEntries REMOVE: ThreadId={ThreadId}, EntityType={EntityType}", threadId, entityType);
                    Entries.Remove(e);
                    _logger.LogDebug("SaveEntries ADD (update): ThreadId={ThreadId}, EntityType={EntityType}", threadId, entityType);
                    Entries.Add(newEntry);
                }
                else
                {
                    _logger.LogDebug("SaveEntries adding new: ThreadId={ThreadId}, EntityType={EntityType}, Id={Id}", threadId, entityType, id ?? "null");

                    var newEntry = new Entry(entry);

                    if (newEntry.Properties == null)
                    {
                        _logger.LogWarning("SaveEntries created Entry with null Properties: ThreadId={ThreadId}, EntityType={EntityType}, State={State}, SourceEntryPropertiesNull={SourceNull}",
                            threadId, entityType, entityState, entry.Properties == null);
                    }

                    _logger.LogDebug("SaveEntries ADD (new): ThreadId={ThreadId}, EntityType={EntityType}", threadId, entityType);
                    Entries.Add(newEntry);
                }
            }
            catch (Exception ex)
            {
                var entityTypeName = "unknown";
                var entityStateStr = "unknown";
                try
                {
                    entityTypeName = entry.Entity?.GetType()?.Name ?? "null";
                    entityStateStr = entry.State.ToString();
                }
                catch { }

                _logger.LogError(ex, "Error processing entry in SaveEntries: ThreadId={ThreadId}, EntityType={EntityType}, State={State}, EntriesCount={Count}",
                    threadId, entityTypeName, entityStateStr, Entries.Count);
            }
        }
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeThreadId, 0, threadId);
        }
    }
}
