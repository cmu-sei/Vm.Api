// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Domain.Services;

public interface IXApiQueueService
{
    Task EnqueueAsync(XApiQueuedStatementEntity statement, CancellationToken ct = default);
    Task<IReadOnlyList<XApiQueuedStatementEntity>> DequeueAsync(int batchSize, CancellationToken ct = default);
    Task MarkCompletedAsync(Guid id, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string errorMessage, bool isTransient, CancellationToken ct = default);
    Task ResetStaleProcessingAsync(DateTime staleBefore, CancellationToken ct = default);
    Task CleanupAsync(DateTime completedBefore, CancellationToken ct = default);
}

public class XApiQueueService : IXApiQueueService
{
    private readonly VmContext _context;
    private readonly ILogger<XApiQueueService> _logger;

    public XApiQueueService(VmContext context, ILogger<XApiQueueService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task EnqueueAsync(XApiQueuedStatementEntity statement, CancellationToken ct = default)
    {
        statement.Id = Guid.NewGuid();
        statement.QueuedAt = DateTime.UtcNow;
        statement.Status = XApiQueueStatus.Pending;
        statement.RetryCount = 0;

        _context.XApiQueuedStatements.Add(statement);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<XApiQueuedStatementEntity>> DequeueAsync(int batchSize, CancellationToken ct = default)
    {
        var statements = await _context.XApiQueuedStatements
            .Where(statement => statement.Status == XApiQueueStatus.Pending)
            .OrderBy(statement => statement.QueuedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var statement in statements)
        {
            statement.Status = XApiQueueStatus.Processing;
            statement.LastAttemptAt = DateTime.UtcNow;
            statement.RetryCount++;
        }

        if (statements.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return statements;
    }

    public async Task MarkCompletedAsync(Guid id, CancellationToken ct = default)
    {
        var statement = await _context.XApiQueuedStatements.FindAsync([id], ct);
        if (statement is null)
        {
            return;
        }

        statement.Status = XApiQueueStatus.Completed;
        statement.ErrorMessage = null;
        await _context.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid id, string errorMessage, bool isTransient, CancellationToken ct = default)
    {
        var statement = await _context.XApiQueuedStatements.FindAsync([id], ct);
        if (statement is null)
        {
            return;
        }

        statement.Status = isTransient ? XApiQueueStatus.Pending : XApiQueueStatus.Failed;
        statement.ErrorMessage = errorMessage;
        await _context.SaveChangesAsync(ct);

        if (isTransient)
        {
            _logger.LogWarning(
                "xAPI statement {StatementId} send attempt {RetryCount} failed and will be retried: {Error}",
                statement.Id,
                statement.RetryCount,
                errorMessage);
        }
        else
        {
            _logger.LogError(
                "xAPI statement {StatementId} failed permanently: {Error}",
                statement.Id,
                errorMessage);
        }
    }

    public async Task ResetStaleProcessingAsync(DateTime staleBefore, CancellationToken ct = default)
    {
        var statements = await _context.XApiQueuedStatements
            .Where(statement =>
                statement.Status == XApiQueueStatus.Processing &&
                statement.LastAttemptAt < staleBefore)
            .ToListAsync(ct);

        foreach (var statement in statements)
        {
            statement.Status = XApiQueueStatus.Pending;
            statement.ErrorMessage = "Previous xAPI sender stopped before completing this statement.";
        }

        if (statements.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task CleanupAsync(DateTime completedBefore, CancellationToken ct = default)
    {
        var statements = await _context.XApiQueuedStatements
            .Where(statement =>
                (statement.Status == XApiQueueStatus.Completed || statement.Status == XApiQueueStatus.Failed) &&
                statement.QueuedAt < completedBefore)
            .ToListAsync(ct);

        if (statements.Count == 0)
        {
            return;
        }

        _context.XApiQueuedStatements.RemoveRange(statements);
        await _context.SaveChangesAsync(ct);
    }
}
