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
    private readonly AsyncLocal<List<Entry>> _entriesStorage = new();

    private List<Entry> Entries
    {
        get => _entriesStorage.Value ??= [];
        set => _entriesStorage.Value = value;
    }

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
        _logger.LogDebug("CreateEvents called. Thread: {ThreadId}",
            Environment.CurrentManagedThreadId);

        var events = new List<INotification>();
        var entries = GetEntries();

        _logger.LogDebug("CreateEvents processing {Count} entries. Thread: {ThreadId}",
            entries.Length, Environment.CurrentManagedThreadId);

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
        try
        {
            // Log total count for debugging
            _logger.LogDebug("GetEntries called. Thread: {ThreadId}, Total entries: {Count}",
                Environment.CurrentManagedThreadId, Entries.Count);

            // First, identify any null entries
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i] == null)
                {
                    _logger.LogError("Found null Entry at index {Index} in GetEntries. Thread: {ThreadId}",
                        i, Environment.CurrentManagedThreadId);
                }
            }

            // Filter out null entries first, then filter by state
            var entries = Entries
                .Where((x, index) =>
                {
                    if (x == null)
                    {
                        _logger.LogError("Null entry found at index {Index} during filtering", index);
                        return false;
                    }

                    try
                    {
                        var entityType = x.Entity?.GetType()?.Name ?? "null";
                        var state = x.State;

                        var isMatch = state == EntityState.Added ||
                                      state == EntityState.Modified ||
                                      state == EntityState.Deleted;

                        _logger.LogDebug("Entry {Index}: Entity={EntityType}, State={State}, Match={IsMatch}",
                            index, entityType, state, isMatch);

                        return isMatch;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error filtering entry at index {Index}. Entry.Entity is null: {IsEntityNull}, Entry.Properties is null: {IsPropertiesNull}",
                            index,
                            x.Entity == null,
                            x.Properties == null);
                        throw;
                    }
                })
                .ToList();

            Entries.Clear();
            return entries.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetEntries. Total entries: {Count}", Entries?.Count ?? 0);
            Entries?.Clear();
            throw;
        }
    }

    /// <summary>
    /// Keeps track of changes across multiple savechanges in a transaction, without duplicates
    /// </summary>
    private void SaveEntries(DbContext db)
    {
        var changeTrackerEntries = db.ChangeTracker.Entries().ToList();
        _logger.LogDebug("SaveEntries called. Thread: {ThreadId}, ChangeTracker entries: {ChangeTrackerCount}, Current Entries list count: {EntriesCount}",
            Environment.CurrentManagedThreadId,
            changeTrackerEntries.Count,
            Entries.Count);

        foreach (var entry in changeTrackerEntries)
        {
            try
            {
                var entityType = entry.Entity?.GetType()?.Name ?? "null";
                var entityState = entry.State;

                _logger.LogDebug("Processing entity: Type={EntityType}, State={EntityState}", entityType, entityState);

                // find value of id property
                var id = entry.Properties
                    .FirstOrDefault(x =>
                        x.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd)?.CurrentValue;

                _logger.LogDebug("Extracted ID for {EntityType}: {Id}", entityType, id ?? "null");

                // find matching existing entry, if any
                Entry e = null;

                if (id != null)
                {
                    _logger.LogDebug("Searching for existing entry with ID={Id}. Current Entries list count: {Count}", id, Entries.Count);

                    // Check for null entries in the list before processing
                    var nullEntryCount = Entries.Count(x => x == null);
                    if (nullEntryCount > 0)
                    {
                        _logger.LogWarning("Found {NullCount} null entries in Entries list before FirstOrDefault", nullEntryCount);
                    }

                    try
                    {
                        e = Entries.FirstOrDefault(x =>
                        {
                            if (x == null)
                            {
                                var nullCount = Entries.Count(e => e != null);
                                _logger.LogError("Encountered null entry during lambda. Thread: {ThreadId}, TotalEntries: {Total}, NonNullCount: {NonNull}",
                                    Environment.CurrentManagedThreadId,
                                    Entries.Count,
                                    nullCount);
                                return false;
                            }

                            if (x.Properties == null)
                            {
                                _logger.LogError("Entry has null Properties collection. Thread: {ThreadId}, Entity type: {EntityType}",
                                    Environment.CurrentManagedThreadId,
                                    x.Entity?.GetType()?.Name ?? "null");
                                return false;
                            }

                            var existingId = x.Properties.FirstOrDefault(y =>
                                y.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd)?.CurrentValue;

                            return id.Equals(existingId);
                        });

                        _logger.LogDebug("Existing entry search result: {Found}, Thread: {ThreadId}",
                            e != null ? "Found" : "Not found",
                            Environment.CurrentManagedThreadId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in FirstOrDefault lambda. Thread: {ThreadId}, ID={Id}, Entries count={Count}",
                            Environment.CurrentManagedThreadId, id, Entries.Count);
                        throw;
                    }
                }

                if (e != null)
                {
                    // if entry already exists, mark which properties were previously modified,
                    // remove old entry and add new one, to avoid duplicates
                    _logger.LogDebug("Updating existing entry for {EntityType} with ID={Id}", entityType, id);

                    var newEntry = new Entry(entry, e, _logger);
                    if (newEntry == null || newEntry.Entity == null)
                    {
                        _logger.LogWarning("Attempted to add null or invalid entry (existing entry path). EntityType: {EntityType}, EntityState: {EntityState}, PropertiesNull: {PropertiesNull}",
                            entityType,
                            entityState,
                            entry?.Properties == null);
                    }
                    else
                    {
                        _logger.LogDebug("Successfully created updated entry. Removing old entry and adding new one. Thread: {ThreadId}",
                            Environment.CurrentManagedThreadId);
                        Entries.Remove(e);
                        Entries.Add(newEntry);

                        // Immediate verification
                        _logger.LogDebug("Added entry to list. Thread: {ThreadId}, Index: {Index}, EntityType: {EntityType}, EntryIsNull: {IsNull}, EntityIsNull: {EntityNull}",
                            Environment.CurrentManagedThreadId,
                            Entries.Count - 1,
                            newEntry.Entity?.GetType()?.Name ?? "null",
                            newEntry == null,
                            newEntry?.Entity == null);

                        var justAdded = Entries[^1];
                        if (justAdded == null)
                        {
                            _logger.LogError("CRITICAL: Entry became null immediately after adding! Thread: {ThreadId}, Index: {Index}",
                                Environment.CurrentManagedThreadId, Entries.Count - 1);
                        }

                        _logger.LogDebug("Entries list count after update: {Count}, Thread: {ThreadId}",
                            Entries.Count, Environment.CurrentManagedThreadId);
                    }
                }
                else
                {
                    _logger.LogDebug("Adding new entry for {EntityType} with ID={Id}", entityType, id ?? "null");

                    var newEntry = new Entry(entry, null, _logger);
                    if (newEntry == null || newEntry.Entity == null)
                    {
                        _logger.LogWarning("Attempted to add null or invalid entry (new entry path). EntityType: {EntityType}, EntityState: {EntityState}, PropertiesNull: {PropertiesNull}",
                            entityType,
                            entityState,
                            entry?.Properties == null);
                    }
                    else
                    {
                        Entries.Add(newEntry);

                        // Immediate verification
                        _logger.LogDebug("Added entry to list. Thread: {ThreadId}, Index: {Index}, EntityType: {EntityType}, EntryIsNull: {IsNull}, EntityIsNull: {EntityNull}",
                            Environment.CurrentManagedThreadId,
                            Entries.Count - 1,
                            newEntry.Entity?.GetType()?.Name ?? "null",
                            newEntry == null,
                            newEntry?.Entity == null);

                        var justAdded = Entries[^1];
                        if (justAdded == null)
                        {
                            _logger.LogError("CRITICAL: Entry became null immediately after adding! Thread: {ThreadId}, Index: {Index}",
                                Environment.CurrentManagedThreadId, Entries.Count - 1);
                        }

                        _logger.LogDebug("Successfully added new entry. Thread: {ThreadId}, Entries list count: {Count}",
                            Environment.CurrentManagedThreadId, Entries.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing entry in SaveEntries. EntityType: {EntityType}, EntityState: {EntityState}",
                    entry?.Entity?.GetType()?.Name ?? "null",
                    entry?.State.ToString() ?? "null");
            }
        }
    }
}
