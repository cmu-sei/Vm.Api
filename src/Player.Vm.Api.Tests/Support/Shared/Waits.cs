// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Crucible.Api.Testing;

/// <summary>
/// Polling for an effect that arrives on a thread the test does not own.
/// </summary>
public static class Waits
{
    /// <summary>
    /// A duration, not a count of attempts, so the wait means the same on a loaded CI runner as on a
    /// developer machine. Generous because it only bounds a hang: a passing test returns on the first
    /// or second poll.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, or fails the test naming <paramref name="what"/>
    /// it was waiting for. A last resort, for background services whose work starts in their
    /// constructor and completes nowhere a test can await; prefer a signal, or an effect that queues
    /// behind a later observable one, wherever one exists.
    /// </summary>
    public static async Task Until(Func<Task<bool>> condition, string what, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var elapsed = Stopwatch.StartNew();

        while (!await condition())
        {
            if (elapsed.Elapsed >= Budget)
            {
                Assert.Fail($"Timed out after {Budget.TotalSeconds:0.#}s waiting for {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        }
    }
}
