// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Tests.Infrastructure;
using VimClient;
using Xunit;
using static Player.Vm.Api.Tests.Infrastructure.FakeChangeFeed;

namespace Player.Vm.Api.Tests;

/// <summary>
/// <c>VsphereMachineWatcher</c>: the long-poll over one vCenter's property-collector change feed that keeps
/// that connection's machine caches current, and reports each changed machine for the persister to write.
/// </summary>
/// <remarks>
/// Driven against <see cref="FakeChangeFeed"/>, which plays a script of <c>WaitForUpdatesEx</c> results and
/// then holds the next call open the way vCenter does. Each test runs the watcher until the script is spent,
/// stops it, and asserts on the connection's caches and on the ids it reported - which is everything the
/// watcher is allowed to touch.
/// </remarks>
public class VsphereMachineWatcherTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private readonly FakeChangeFeed _feed = new();
    private readonly VsphereConnection _connection;
    private readonly ConcurrentQueue<Guid> _changed = new();
    private int _reconnects;

    public VsphereMachineWatcherTests()
    {
        _connection = new VsphereConnection(new VsphereHost { Address = "vcenter" }, new VsphereOptions(), NullLogger.Instance)
        {
            ClientFactory = _ => _feed.Client
        };
        _connection.Client = _feed.Client;
        _connection.Sic = ServiceContent();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Initial snapshot

    /// <summary>
    /// The first call returns every machine, possibly over several truncated pages. Pruning waits for the
    /// last page, so a machine that only arrives on page two is never dropped in between.
    /// </summary>
    [Fact]
    public async Task InitialSnapshot_AcrossTruncatedPages_CachesEveryMachineAndPrunesOnlyTheUnseen()
    {
        _connection.UpsertMachine(Machine(B, "vm-2"));
        _connection.UpsertMachine(Machine(C, "vm-gone"));
        _feed.Then(Page("1", truncated: true, Enter("vm-1", A)))
             .Then(Page("2", Enter("vm-2", B)));

        await Watch();

        Assert.Equal([A, B], _connection.MachineStates.Keys.Order());
        Assert.Equal([A, B], _connection.MachineCache.Keys.Order());
        Assert.Equal(["vm-1", "vm-2"], _connection.VmGuids.Keys.Order());
        Assert.Equal(["", "1", "2"], _feed.Versions);
        Assert.Equal([A, B], _changed);
    }

    [Fact]
    public async Task InitialSnapshot_ClearsAPreviousWatcherError()
    {
        _connection.WatcherError = "earlier failure";
        _feed.Then(Page("1", Enter("vm-1", A)));

        await Watch();

        Assert.Null(_connection.WatcherError);
    }

    /// <summary>
    /// Without the Specified flags XmlSerializer leaves the values out and vCenter gets an empty
    /// <c>WaitOptions</c> - an unbounded wait - and an <c>ObjectSpec</c> that reports the view itself.
    /// </summary>
    [Fact]
    public void RequestSpecs_SerializeTheirBounds()
    {
        var options = Serialize(VsphereMachineWatcher.BuildWaitOptions());
        var filter = Serialize(VsphereMachineWatcher.BuildFilterSpec(Mor("ContainerView", "view-1")));

        Assert.Contains($">{VsphereMachineWatcher.MaxWaitSeconds}</maxWaitSeconds>", options);
        Assert.Contains($">{VsphereMachineWatcher.MaxObjectUpdates}</maxObjectUpdates>", options);
        Assert.Contains("<skip>true</skip>", filter);
        Assert.Contains("traverseView", filter);
    }

    #endregion

    #region Changes

    [Fact]
    public async Task Modify_UpdatesPowerStateAndAddresses()
    {
        _feed.Then(Page("1", Enter("vm-1", A)))
             .Then(Page("2", Modify("vm-1", Power(VirtualMachinePowerState.poweredOn), Ips("10.0.0.5", "fe80::1"))));

        await Watch();

        var machine = _connection.MachineStates[A];
        Assert.Equal("on", machine.State);
        Assert.Equal(["10.0.0.5", "fe80::1"], machine.IpAddresses);
        Assert.Equal([A, A], _changed);
    }

    /// <summary>A property vCenter failed to read is not a property that became empty.</summary>
    [Fact]
    public async Task MissingProperty_KeepsItsPreviousValue()
    {
        _feed.Then(Page("1", Enter("vm-1", A, ips: ["10.0.0.5"], snapshot: true)));
        var update = Modify("vm-1", Power(VirtualMachinePowerState.poweredOn));
        update.missingSet = [new MissingProperty { path = "guest.net" }, new MissingProperty { path = "rootSnapshot" }];
        _feed.Then(Page("2", update));

        await Watch();

        var machine = _connection.MachineStates[A];
        Assert.Equal("on", machine.State);
        Assert.Equal(["10.0.0.5"], machine.IpAddresses);
        Assert.True(machine.HasSnapshot);
    }

    [Fact]
    public async Task Leave_RemovesTheMachineFromEveryCache()
    {
        _feed.Then(Page("1", Enter("vm-1", A)))
             .Then(Page("2", Leave("vm-1")));

        await Watch();

        Assert.Empty(_connection.MachineStates);
        Assert.Empty(_connection.MachineCache);
        Assert.Empty(_connection.VmGuids);
    }

    /// <summary>
    /// A machine unregistered and registered again keeps its uuid under a new moref. The old moref's
    /// leave can arrive after the new one's enter, and must not take the new mapping with it.
    /// </summary>
    [Fact]
    public async Task Leave_OfAStaleReference_KeepsTheNewerMapping()
    {
        _feed.Then(Page("1", Enter("vm-1", A)))
             .Then(Page("2", Enter("vm-7", A, VirtualMachinePowerState.poweredOn)))
             .Then(Page("3", Leave("vm-1")));

        await Watch();

        Assert.Equal("vm-7", _connection.MachineCache[A].Value);
        Assert.Equal("vm-7", _connection.MachineStates[A].Reference.Value);
        Assert.Equal("on", _connection.MachineStates[A].State);
        Assert.Equal(["vm-7"], _connection.VmGuids.Keys);
    }

    [Fact]
    public async Task UuidChange_MovesTheMachineToItsNewId()
    {
        _feed.Then(Page("1", Enter("vm-1", A)))
             .Then(Page("2", Modify("vm-1", Assign("config.uuid", B.ToString()))));

        await Watch();

        Assert.Equal([B], _connection.MachineStates.Keys);
        Assert.Equal([B], _connection.MachineCache.Keys);
        Assert.Equal(B, _connection.VmGuids["vm-1"]);
    }

    /// <summary>A machine with no readable uuid cannot be matched to a Vm row, so it is not cached.</summary>
    [Fact]
    public async Task MachineWithoutAValidUuid_IsNotCached()
    {
        var update = Enter("vm-1", A);
        update.changeSet = update.changeSet.Where(x => x.name != "config.uuid").Append(Assign("config.uuid", "not-a-guid")).ToArray();
        _feed.Then(Page("1", update));

        await Watch();

        Assert.Empty(_connection.MachineStates);
        Assert.Empty(_changed);
    }

    /// <summary>A timed-out wait is not a reset: the next call resumes from the same version.</summary>
    [Fact]
    public async Task Timeout_KeepsWaitingFromTheSameVersion()
    {
        _feed.Then(Page("1", Enter("vm-1", A)))
             .ThenTimeout()
             .Then(Page("2", Modify("vm-1", Power(VirtualMachinePowerState.poweredOn))));

        await Watch();

        Assert.Equal(["", "1", "1", "2"], _feed.Versions);
        Assert.Equal("on", _connection.MachineStates[A].State);
        Assert.Equal(1, _feed.ViewsCreated);
    }

    [Theory]
    [InlineData(VirtualMachinePowerState.poweredOn, "on")]
    [InlineData(VirtualMachinePowerState.poweredOff, "off")]
    [InlineData(VirtualMachinePowerState.suspended, "suspended")]
    public void PowerState_MapsToTheCachedState(VirtualMachinePowerState power, string expected)
    {
        var machine = VsphereMachineWatcher.ToMachine(
            Mor("VirtualMachine", "vm-1"), A, new Dictionary<string, object>
            {
                ["summary.runtime.powerState"] = power
            });

        Assert.Equal(expected, machine.State);
    }

    [Fact]
    public void PowerState_ThatWasNeverRead_IsUnknown()
    {
        var machine = VsphereMachineWatcher.ToMachine(Mor("VirtualMachine", "vm-1"), A, new Dictionary<string, object>());

        Assert.Equal("unknown", machine.State);
        Assert.Empty(machine.IpAddresses);
        Assert.False(machine.HasSnapshot);
    }

    #endregion

    #region Faults

    /// <summary>
    /// A fault that is not about the session backs off and rebuilds from a fresh snapshot, without
    /// asking the connection to log in again.
    /// </summary>
    [Fact]
    public async Task Fault_BacksOffAndRebuilds()
    {
        _feed.Then(Page("1", Enter("vm-1", A)))
             .ThenThrow(new FaultException("The object has already been deleted or has not been completely created"))
             .Then(Page("1", Enter("vm-1", A, VirtualMachinePowerState.poweredOn)));

        await Watch();

        Assert.Equal(2, _feed.ViewsCreated);
        Assert.Equal(["", "1", "", "1"], _feed.Versions);
        Assert.Equal("on", _connection.MachineStates[A].State);
        Assert.Null(_connection.WatcherError);
        Assert.Equal(0, _reconnects);
    }

    /// <summary>The error stays visible to the health check until a snapshot completes again.</summary>
    [Fact]
    public async Task Fault_IsReportedUntilASnapshotCompletes()
    {
        _feed.ThenThrow(new FaultException("boom"));

        await Watch();

        Assert.Equal("boom", _connection.WatcherError);
    }

    [Fact]
    public async Task NotAuthenticated_AsksTheConnectionToLogInAgain()
    {
        _feed.ThenThrow(new FaultException<NotAuthenticated>(new NotAuthenticated(), new FaultReason("The session is not authenticated.")));

        await Watch();

        Assert.Equal(1, _reconnects);
    }

    /// <summary>
    /// The connection loop finds the session gone, logs in on a new client and retires the old one, which
    /// faults the pending long-poll. The watcher sees the generation move and rebuilds on the new client
    /// at once, without destroying objects that went with the old session.
    /// </summary>
    [Fact]
    public async Task SessionReplaced_RebuildsOnTheNewClient()
    {
        var replacement = new FakeChangeFeed();
        var clients = new ConcurrentQueue<IVimClient>([_feed.Client, replacement.Client]);
        var connection = new VsphereConnection(
            new VsphereHost { Address = "vcenter", Username = "u", Password = "p" }, new VsphereOptions(), NullLogger.Instance)
        {
            ClientFactory = _ => clients.TryDequeue(out var client) ? client : throw new InvalidOperationException("No more clients")
        };
        await connection.Load();

        _feed.Then(Page("1", Enter("vm-1", A)));
        replacement.Then(Page("1", Enter("vm-1", A, VirtualMachinePowerState.poweredOn)));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = Watcher(connection).RunAsync(cts.Token);

        await PollLoop.Until(() => _feed.Idle, "the first session's snapshot");
        _feed.SessionValid = false;
        await connection.Load();
        await PollLoop.Until(() => replacement.Idle, "the replacement session's snapshot");

        cts.Cancel();
        await run;

        Assert.Equal(2, connection.Generation);
        Assert.Equal("on", connection.MachineStates[A].State);
        Assert.Equal(1, replacement.ViewsCreated);
        await _feed.Client.DidNotReceive().DestroyPropertyCollectorAsync(Arg.Any<ManagedObjectReference>());
        await replacement.Client.Received(1).DestroyPropertyCollectorAsync(Arg.Any<ManagedObjectReference>());
        Assert.Equal(0, _reconnects);
    }

    [Theory]
    [InlineData(typeof(CommunicationException), true)]
    [InlineData(typeof(TimeoutException), false)]
    [InlineData(typeof(InvalidOperationException), false)]
    public void IsSessionLost_ClassifiesTransportFailures(Type type, bool expected)
    {
        Assert.Equal(expected, VsphereMachineWatcher.IsSessionLost((Exception)Activator.CreateInstance(type)));
    }

    #endregion

    #region Shutdown

    /// <summary>
    /// Stopping releases the pending long-poll on the server and destroys the collector and view, so a
    /// stopped watcher leaves nothing open on vCenter.
    /// </summary>
    [Fact]
    public async Task Stopping_CancelsThePendingWaitAndDestroysWhatItCreated()
    {
        _feed.Then(Page("1", Enter("vm-1", A)));

        await Watch();

        await _feed.Client.Received(1).CancelWaitForUpdatesAsync(Arg.Any<ManagedObjectReference>());
        await _feed.Client.Received(1).DestroyPropertyCollectorAsync(Arg.Any<ManagedObjectReference>());
        await _feed.Client.Received(1).DestroyViewAsync(Arg.Any<ManagedObjectReference>());
    }

    #endregion

    private VsphereMachineWatcher Watcher(VsphereConnection connection = null) =>
        new(connection ?? _connection, _changed.Enqueue, () => Interlocked.Increment(ref _reconnects),
            () => TimeSpan.FromMilliseconds(20), NullLogger.Instance)
        {
            InitialBackoff = TimeSpan.FromMilliseconds(10),
            DisconnectedPollInterval = TimeSpan.FromMilliseconds(10)
        };

    // Runs the watcher until it has taken every scripted step and is waiting on the next, then stops it.
    private async Task Watch()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = Watcher().RunAsync(cts.Token);

        await PollLoop.Until(() => _feed.Idle || run.IsCompleted, "the watcher to take every scripted step");

        cts.Cancel();
        await run;
    }

    private static VsphereVirtualMachine Machine(Guid id, string reference) => new()
    {
        Id = id,
        Reference = Mor("VirtualMachine", reference),
        State = "off",
        IpAddresses = []
    };

    private static string Serialize<T>(T value)
    {
        using var writer = new StringWriter();
        new XmlSerializer(typeof(T)).Serialize(writer, value);
        return writer.ToString();
    }
}
