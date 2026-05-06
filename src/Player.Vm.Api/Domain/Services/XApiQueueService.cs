// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
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
    Task<XApiQueuedStatementEntity> DequeueAsync(CancellationToken ct = default);
    Task MarkCompletedAsync(Guid id, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string errorMessage, CancellationToken ct = default);
    Task CleanupOldStatementsAsync(TimeSpan olderThan, CancellationToken ct = default);
}

public class XApiQueueService : IXApiQueueService
{
    private readonly VmContext _context;
    private readonly ILogger<XApiQueueService> _logger;
    private const int MaxRetries = 5;

    public XApiQueueService(
        VmContext context,
        ILogger<XApiQueueService> logger)
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
        await context.SaveChangesAsync(ct);

        _logger.LogDebug("Enqueued xAPI statement {StatementId} with verb {Verb}", statement.Id, statement.Verb);
    }

    public async Task<XApiQueuedStatementEntity> DequeueAsync(CancellationToken ct = default)
    {
        var statement = await context.XApiQueuedStatements
            .Where(s => s.Status == XApiQueueStatus.Pending && s.RetryCount < MaxRetries)
            .OrderBy(s => s.QueuedAt)
            .FirstOrDefaultAsync(ct);

        return statement;
    }

    public async Task MarkCompletedAsync(Guid id, CancellationToken ct = default)
    {
        var statement = await context.XApiQueuedStatements.FindAsync(new object[] { id }, ct);
        if (statement != null)
        {
            statement.Status = XApiQueueStatus.Completed;
            statement.LastAttemptAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);

            _logger.LogDebug("Marked xAPI statement {StatementId} as completed", id);
        }
    }

    public async Task MarkFailedAsync(Guid id, string errorMessage, CancellationToken ct = default)
    {
        var statement = await context.XApiQueuedStatements.FindAsync(new object[] { id }, ct);
        if (statement != null)
        {
            statement.RetryCount++;
            statement.LastAttemptAt = DateTime.UtcNow;
            statement.ErrorMessage = errorMessage;

            if (statement.RetryCount >= MaxRetries)
            {
                statement.Status = XApiQueueStatus.Failed;
                _logger.LogWarning("xAPI statement {StatementId} failed after {RetryCount} attempts: {Error}",
                    id, statement.RetryCount, errorMessage);
            }
            else
            {
                _logger.LogDebug("xAPI statement {StatementId} attempt {RetryCount} failed: {Error}",
                    id, statement.RetryCount, errorMessage);
            }

            await context.SaveChangesAsync(ct);
        }
    }

    public async Task CleanupOldStatementsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoffDate = DateTime.UtcNow - olderThan;
        var oldStatements = await context.XApiQueuedStatements
            .Where(s => (s.Status == XApiQueueStatus.Completed || s.Status == XApiQueueStatus.Failed)
                     && s.QueuedAt < cutoffDate)
            .ToListAsync(ct);

        if (oldStatements.Any())
        {
            _context.XApiQueuedStatements.RemoveRange(oldStatements);
            await context.SaveChangesAsync(ct);

            _logger.LogInformation("Cleaned up {Count} old xAPI statements", oldStatements.Count);
        }
    }
}
