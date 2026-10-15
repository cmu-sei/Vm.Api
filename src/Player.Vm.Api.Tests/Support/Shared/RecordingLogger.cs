// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Crucible.Api.Testing;

/// <summary>
/// A logger that keeps what it was told, for the code whose only output is a log entry.
/// </summary>
/// <remarks>
/// Three members, so a hand-written recorder beats a substitute, and unlike <c>NullLogger</c> it can be
/// asserted on. Records the formatted message, which is what an operator reads, and the exception,
/// because background services swallow theirs and the level and the exception are then the only
/// evidence that anything went wrong. Concurrent, because the code that logs often runs on its own
/// thread.
/// </remarks>
public class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>A snapshot of what was logged, in order.</summary>
    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    /// <summary>The entries at one level, which is how "logged, and loudly enough" is asked.</summary>
    public IReadOnlyList<LogEntry> At(LogLevel level) => [.. _entries.Where(x => x.Level == level)];

    public IDisposable BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter) =>
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));

    public sealed record LogEntry(LogLevel Level, string Message, Exception Exception);
}

/// <summary>The same recorder where the collaborator asks for <see cref="ILogger{TCategoryName}"/>.</summary>
public sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>
{
}
