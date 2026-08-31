// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Player.Vm.Api.Data;
using Xunit;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// Runs one of the polling background services for a counted number of passes and then stops it, so that
/// a loop with no return value and no completion signal can be asserted on.
/// </summary>
/// <remarks>
/// <para>
/// The four pollers - <c>MachineStateService</c>, vSphere's <c>TaskService</c>,
/// <c>ProxmoxTaskService</c> and <c>ProxmoxStateService</c> - share one shape: a <c>while</c> over a
/// cancellation token, a scope created per turn, all of the work inside a <c>try</c> that logs and
/// swallows, and then a wait on an <c>AsyncAutoResetEvent</c> which the service's own public method sets.
/// Nothing about a turn is observable from outside, which is why every other test in this suite
/// substitutes these services away.
/// </para>
/// <para>
/// This harness is the service provider they are handed, and that is what makes a turn countable: each
/// one creates exactly one scope, so <see cref="Passes"/> is the number of turns that have <em>started</em>
/// and a scope asked for after <see cref="AllowedPasses"/> means the turn before it finished - including
/// its database writes and its SignalR broadcasts, both of which happen inside the scope. Asking for one
/// pass too many is refused with an exception, which each service's own <c>catch</c> logs and ignores, so
/// the extra turn does no work and cannot disturb what the test is about to assert.
/// </para>
/// <para>
/// Timing is not what advances the loop here. Configure the poll interval long - a minute - and call
/// <see cref="Run"/>, which nudges the service's own "check now" method until the passes it was asked for
/// have happened. What the loop does is then deterministic rather than a race against a stopwatch. The
/// exception is a test whose subject <em>is</em> the interval, which has to let the wait elapse; see
/// <see cref="RunUnprompted"/>.
/// </para>
/// </remarks>
public sealed class PollLoop : IServiceProvider, IServiceScopeFactory
{
    private readonly Func<VmContext> _newContext;
    private readonly object[] _scoped;
    private int _passes;

    /// <param name="newContext">
    /// A fresh <see cref="VmContext"/> per pass, as the production registration gives one: the scope owns
    /// it and disposes it, so a test reads what a pass wrote through a context of its own.
    /// </param>
    /// <param name="scoped">
    /// Whatever else the pass resolves from its scope - <c>IVsphereService</c>, <c>IProxmoxService</c> -
    /// matched by any interface it implements.
    /// </param>
    public PollLoop(Func<VmContext> newContext, params object[] scoped)
    {
        _newContext = newContext;
        _scoped = scoped;
    }

    /// <summary>
    /// How many passes may do work. A later one is refused at scope creation. Set by <see cref="Run"/>
    /// and <see cref="RunUnprompted"/>; assign it directly only for a test that starts the service itself.
    /// </summary>
    public int AllowedPasses { get; set; } = 1;

    /// <summary>
    /// Passes started, refused ones included - which is what makes a refusal a barrier. Not a count of
    /// passes that did work: assert that with <see cref="Contexts"/>, or on the collaborator a pass calls.
    /// The number of refusals is not fixed, because <see cref="Stop"/>'s nudge can let one more start.
    /// </summary>
    public int Passes => Volatile.Read(ref _passes);

    /// <summary>Every context handed to a pass, so a test can tell a fresh one from a reused one.</summary>
    public List<VmContext> Contexts { get; } = [];

    public object GetService(Type serviceType) =>
        serviceType == typeof(IServiceScopeFactory) ? this : Resolve(serviceType);

    public IServiceScope CreateScope()
    {
        if (Interlocked.Increment(ref _passes) > AllowedPasses)
        {
            // Refused rather than allowed and ignored: a pass that ran would write to the database the
            // test is about to read. Every one of these services catches around its whole scope.
            throw new InvalidOperationException(
                $"PollLoop allows {AllowedPasses} pass(es) and this is number {Passes}.");
        }

        var context = _newContext();
        Contexts.Add(context);

        return new Scope(context, this);
    }

    /// <summary>
    /// Runs <paramref name="passes"/> passes and stops, nudging <paramref name="checkNow"/> - the
    /// service's own <c>CheckTasks</c> or <c>CheckState</c> - so that no poll interval has to elapse.
    /// </summary>
    /// <remarks>
    /// Nudging in the wait loop rather than once is what makes more than one pass work: the reset event
    /// is an auto-reset, so two <c>Set</c> calls before a single wait are one signal, not two. It also
    /// means these tests cover <c>CheckTasks</c>/<c>CheckState</c> by construction - the loop would never
    /// turn a second time without them.
    /// </remarks>
    public async Task Run(BackgroundService service, Action checkNow, int passes = 1, int seconds = 10)
    {
        AllowedPasses = passes;

        await service.StartAsync(Ct);

        try
        {
            await Until(
                () =>
                {
                    checkNow();

                    return Passes > passes;
                },
                $"{passes} pass(es) of {service.GetType().Name}",
                seconds);
        }
        finally
        {
            await Stop(service, checkNow);
        }
    }

    /// <summary>
    /// Starts the service and waits for <paramref name="passes"/> passes to <em>start</em> without ever
    /// nudging it, so the only thing that can advance the loop is the interval it chose for itself. For
    /// the tests about that choice, and the only ones here that depend on wall clock.
    /// </summary>
    /// <remarks>
    /// Configure the interval the test expects to be chosen as a handful of milliseconds and the other as
    /// a minute. Then a second pass arriving at all is the assertion, the margin between passing and
    /// failing is four orders of magnitude, and swapping the two intervals in the service fails the test
    /// by timing out rather than by a hair.
    /// </remarks>
    public async Task RunUnprompted(
        BackgroundService service, Action checkNow, int passes = 2, int seconds = 10)
    {
        AllowedPasses = passes;

        await service.StartAsync(Ct);

        try
        {
            await Until(() => Passes >= passes, $"pass {passes} of {service.GetType().Name} to start", seconds);
        }
        finally
        {
            await Stop(service, checkNow);
        }
    }

    /// <summary>Waits, bounded, for something a loop on another thread has done.</summary>
    /// <remarks>
    /// A condition that never becomes true has to fail rather than hang: these services swallow their own
    /// exceptions, so the difference between "the pass did nothing" and "the pass threw" is a message in
    /// the recorded log, and a hung test shows neither.
    /// </remarks>
    public static async Task Until(Func<bool> condition, string what, int seconds = 10)
    {
        var clock = Stopwatch.StartNew();

        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds)
            {
                Assert.Fail($"Waited {seconds}s for {what} and it did not happen.");
            }

            await Task.Delay(10, Ct);
        }
    }

    /// <summary>
    /// Cancels the loop and waits for it to leave, nudging it once more after the cancellation so that it
    /// does not sit out the rest of its interval first.
    /// </summary>
    /// <remarks>
    /// The nudge is not a nicety for vSphere's <c>TaskService</c>: it is the one poller whose
    /// <c>WaitAsync</c> is given no cancellation token, so cancelling alone leaves it asleep for up to a
    /// full <c>CheckTaskProgressIntervalMilliseconds</c> - characterized in <c>TaskServiceTests</c>. The
    /// order matters and is safe: <c>StopAsync</c> requests cancellation synchronously, before the task
    /// it returns is awaited, so by the time the nudge lands the loop is already on its way out.
    /// </remarks>
    private static async Task Stop(BackgroundService service, Action checkNow)
    {
        var stopping = service.StopAsync(CancellationToken.None);

        checkNow();

        await stopping;
    }

    private object Resolve(Type serviceType) =>
        _scoped.FirstOrDefault(serviceType.IsInstanceOfType);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// One pass's scope. Written out rather than substituted because the scope owns what it resolves: a
    /// context per pass left open would hold a pooled connection for the rest of the run, and one
    /// PostgreSQL server serves the whole suite.
    /// </summary>
    private sealed class Scope(VmContext context, PollLoop loop) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;

        public object GetService(Type serviceType) =>
            serviceType == typeof(VmContext) ? context : loop.GetService(serviceType);

        public void Dispose() => context.Dispose();
    }
}
