// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// A logger that keeps what it was told, for the code whose only output is a log entry.
/// </summary>
/// <remarks>
/// Three members, so a hand-written recorder beats a substitute - and unlike <c>NullLogger</c> it can be
/// asserted on. Records the formatted message, which is what an operator actually reads, and the
/// exception, because the background pollers swallow theirs and the level and the exception are then the
/// only evidence that anything went wrong.
/// </remarks>
public class RecordingLogger : ILogger
{
    public List<LogEntry> Entries { get; } = [];

    /// <summary>The entries at one level, which is how "logged, and loudly enough" is asked.</summary>
    public IEnumerable<LogEntry> At(LogLevel level) => Entries.Where(x => x.Level == level);

    public IDisposable BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter) =>
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));

    public sealed record LogEntry(LogLevel Level, string Message, Exception Exception);
}

/// <summary>
/// The same recorder where the collaborator asks for <see cref="ILogger{TCategoryName}"/>, which every
/// service in this application does.
/// </summary>
public sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>
{
}
