// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// A host identical to <see cref="VmApiFactory"/> except that <c>VmUsageLogging:Enabled</c> is true.
/// </summary>
/// <remarks>
/// <para>
/// A whole second host for one boolean, because that boolean is not something a running host can be
/// asked to change: <c>Startup.ConfigureServices</c> reads it once to choose which
/// <c>IVmUsageLoggingService</c> to register, and <c>VmUsageLoggingSessionController</c> reads
/// <c>IOptionsMonitor&lt;VmUsageLoggingOptions&gt;.CurrentValue</c> in its constructor rather than
/// per request. Rewriting configuration mid-run would leave the first of those stale and prove nothing
/// about a real deployment.
/// </para>
/// <para>
/// The default stays false so the other endpoint test classes keep running against the shipped
/// configuration - see <see cref="VmApiFactory.VmUsageLoggingEnabled"/>. The cost is one more host
/// startup, which is why both sides of the flag are covered by one class each rather than per test.
/// </para>
/// </remarks>
public class VmUsageLoggingEnabledFactory(DatabaseFixture database) : VmApiFactory(database)
{
    protected override bool VmUsageLoggingEnabled => true;
}
