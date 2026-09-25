// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Vsphere.Models;
using VimClient;
using Action = System.Action;

namespace Player.Vm.Api.Domain.Vsphere.Services;

/// <summary>
/// Keeps one vCenter's machine caches current from the property collector's change feed.
/// </summary>
/// <remarks>
/// A container view over every VirtualMachine feeds a dedicated property collector, and
/// WaitForUpdatesEx long-polls it: the first call returns every machine, and each later call returns
/// only the machines that appeared, changed a watched property, or went away. A burst of new VMs
/// therefore costs a handful of calls rather than one per VM. The watcher runs on the connection's
/// shared session and never writes to the database; it reports each changed machine id to
/// ConnectionService, whose persister writes the latest cached state.
/// </remarks>
public sealed class VsphereMachineWatcher
{
    internal static readonly string[] WatchedPaths =
        ["name", "config.uuid", "summary.runtime.powerState", "guest.net", "rootSnapshot"];

    internal const int MaxWaitSeconds = 30;
    internal const int MaxObjectUpdates = 500;

    private readonly VsphereConnection _connection;
    private readonly Action<Guid> _changed;
    private readonly Action _requestReconnect;
    private readonly Func<TimeSpan> _maxBackoff;
    private readonly Func<TimeSpan> _resnapshotInterval;
    private readonly ILogger _logger;

    // Property values by moref, as the change feed last reported them.
    private readonly Dictionary<string, Dictionary<string, object>> _objects = new();

    public VsphereMachineWatcher(
        VsphereConnection connection,
        Action<Guid> changed,
        Action requestReconnect,
        Func<TimeSpan> maxBackoff,
        Func<TimeSpan> resnapshotInterval,
        ILogger logger)
    {
        _connection = connection;
        _changed = changed;
        _requestReconnect = requestReconnect;
        _maxBackoff = maxBackoff;
        _resnapshotInterval = resnapshotInterval;
        _logger = logger;
    }

    internal TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
            // Taken before the session is read, so a login in between completes the signal held here.
            var connected = _connection.WhenConnected();
            var session = _connection.Current;

            if (session == null)
            {
                // The signal is the fast path. The bound means a missed one costs a retry interval, not
                // the watcher.
                await WaitAsync(connected, MaxBackoff(), ct);
                continue;
            }

            try
            {
                await WatchAsync(session, () => backoff = InitialBackoff, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(_connection.Current, session))
                {
                    // The session was replaced or dropped under the pending call. Start again on whatever
                    // the connection has now.
                    _logger.LogInformation("Session for {Host} was replaced. Rebuilding the machine watcher.", _connection.Address);
                    continue;
                }

                _connection.WatcherError = ex.Message;

                if (IsSessionLost(ex))
                {
                    _requestReconnect();
                }

                _logger.LogWarning(ex, "Machine watcher for {Host} failed. Retrying in {Delay}.", _connection.Address, backoff);
                await Delay(backoff, ct);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff().Ticks));
            }
        }
    }

    private TimeSpan MaxBackoff()
    {
        var max = _maxBackoff();
        return max > InitialBackoff ? max : InitialBackoff;
    }

    private async Task WatchAsync(VsphereSession session, Action onHealthy, CancellationToken ct)
    {
        var client = session.Client;
        ManagedObjectReference view = null;
        ManagedObjectReference collector = null;

        try
        {
            // Bounded by the token, so a stalled vCenter cannot hold up a watcher being stopped. Whatever an
            // abandoned call creates goes with the session, which every path that stops a watcher logs out.
            view = (await client.CreateContainerViewAsync(
                session.Sic.viewManager, session.Sic.rootFolder, ["VirtualMachine"], true).WaitAsync(ct)).returnval;
            collector = await client.CreatePropertyCollectorAsync(session.Sic.propertyCollector).WaitAsync(ct);
            await client.CreateFilterAsync(collector, BuildFilterSpec(view), false).WaitAsync(ct);

            _objects.Clear();
            var seen = new HashSet<string>();
            var initial = true;
            var resnapshot = false;
            var snapshotAt = TimeSpan.Zero;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var version = "";
            var options = BuildWaitOptions();

            while (ReferenceEquals(_connection.Current, session))
            {
                // Everything else here follows the change feed, so anything it missed would stay missed
                // until that machine next changed. An empty version asks the same collector for the whole
                // current state again, which the snapshot path applies and prunes against as it did the
                // first time; the persister then writes only the rows that differ. A zero interval turns it off.
                var interval = _resnapshotInterval();

                if (!initial && interval > TimeSpan.Zero && clock.Elapsed - snapshotAt >= interval)
                {
                    _objects.Clear();
                    seen = new HashSet<string>();
                    initial = true;
                    resnapshot = true;
                    version = "";
                }

                var update = await WaitAsync(client, collector, version, options, ct);

                if (update != null)
                {
                    version = update.version;
                    Apply(update, initial ? seen : null, resnapshot);

                    if (update.truncatedSpecified && update.truncated)
                    {
                        continue;
                    }
                }

                if (initial)
                {
                    Prune(seen, resnapshot);
                    _connection.WatcherError = null;
                    onHealthy();
                    _logger.Log(resnapshot ? LogLevel.Debug : LogLevel.Information,
                        "Watching {Count} machines on {Host}", _objects.Count, _connection.Address);

                    initial = false;
                    resnapshot = false;
                    snapshotAt = clock.Elapsed;
                }
            }
        }
        finally
        {
            // Objects made under a session that has since been replaced went with it.
            if (ReferenceEquals(_connection.Current, session))
            {
                if (collector != null)
                {
                    await Bounded(() => client.DestroyPropertyCollectorAsync(collector));
                }

                if (view != null)
                {
                    await Bounded(() => client.DestroyViewAsync(view));
                }
            }
        }
    }

    private async Task<UpdateSet> WaitAsync(
        IVimClient client, ManagedObjectReference collector, string version, WaitOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = client.WaitForUpdatesExAsync(collector, version, options);

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetResult()))
        {
            if (await Task.WhenAny(call, cancelled.Task) == call)
            {
                return await call;
            }
        }

        // Shutting down: release the pending call rather than leave it open on the server.
        await Bounded(() => client.CancelWaitForUpdatesAsync(collector));
        await Bounded(() => call);
        ct.ThrowIfCancellationRequested();
        return null;
    }

    private void Apply(UpdateSet update, HashSet<string> seen, bool resnapshot)
    {
        foreach (var filter in update.filterSet ?? [])
        {
            foreach (var missing in filter.missingSet ?? [])
            {
                _logger.LogDebug("Machine watcher for {Host} could not read {Reference}: {Fault}",
                    _connection.Address, missing.obj?.Value, missing.fault?.localizedMessage);
            }

            foreach (var objectUpdate in filter.objectSet ?? [])
            {
                try
                {
                    Apply(objectUpdate, seen, resnapshot);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error applying update for {Reference} on {Host}", objectUpdate.obj?.Value, _connection.Address);
                }
            }
        }
    }

    private void Apply(ObjectUpdate update, HashSet<string> seen, bool resnapshot)
    {
        var reference = update.obj.Value;

        if (update.kind == ObjectUpdateKind.leave)
        {
            seen?.Remove(reference);

            // The database row keeps its last state, as it did when the full reload dropped a machine.
            if (_objects.Remove(reference, out var gone) && TryGetId(gone, out var goneId))
            {
                _connection.RemoveMachine(goneId, reference);
            }

            return;
        }

        seen?.Add(reference);

        if (!_objects.TryGetValue(reference, out var properties))
        {
            _objects[reference] = properties = new Dictionary<string, object>();
        }

        var hadId = TryGetId(properties, out var oldId);

        foreach (var change in update.changeSet ?? [])
        {
            if (change.op == PropertyChangeOp.assign)
            {
                properties[change.name] = change.val;
            }
            else
            {
                // partialUpdates is off, so collection edits should arrive as whole assignments.
                // Anything else means the value is being replaced; drop it until the next assign.
                _logger.LogDebug("Unexpected {Op} of {Property} on {Reference}", change.op, change.name, reference);
                properties.Remove(change.name);
            }
        }

        // A property vCenter could not read keeps its previous value rather than being cleared.
        foreach (var missing in update.missingSet ?? [])
        {
            _logger.LogDebug("Could not read {Property} on {Reference}: {Fault}",
                missing.path, reference, missing.fault?.localizedMessage);
        }

        var hasId = TryGetId(properties, out var id);

        if (hadId && (!hasId || oldId != id))
        {
            _connection.RemoveMachine(oldId, reference);
        }

        if (!hasId)
        {
            if (update.kind == ObjectUpdateKind.enter)
            {
                _logger.LogDebug("Machine {Reference} on {Host} has no valid uuid", reference, _connection.Address);
            }

            return;
        }

        var machine = ToMachine(update.obj, id, properties);

        if (resnapshot && (!_connection.MachineStates.TryGetValue(id, out var cached) || !SameState(cached, machine)))
        {
            _logger.LogWarning("Re-reading {Host} corrected the cached state of machine {Id} ({Reference})",
                _connection.Address, id, reference);
        }

        _connection.UpsertMachine(machine);
        _changed(id);
    }

    // After a snapshot: forget every moref this host had cached that the snapshot did not include, and any
    // machine whose moref now maps to another uuid.
    private void Prune(HashSet<string> seen, bool resnapshot)
    {
        foreach (var (reference, id) in _connection.VmGuids.ToArray())
        {
            if (!seen.Contains(reference))
            {
                if (resnapshot)
                {
                    _logger.LogWarning("Re-reading {Host} found machine {Id} ({Reference}) gone", _connection.Address, id, reference);
                }

                _connection.RemoveMachine(id, reference);
            }
        }

        foreach (var (id, machine) in _connection.MachineStates.ToArray())
        {
            if (!_connection.VmGuids.TryGetValue(machine.Reference.Value, out var mapped) || mapped != id)
            {
                _connection.RemoveMachine(id, machine.Reference.Value);
            }
        }
    }

    private static bool SameState(VsphereVirtualMachine a, VsphereVirtualMachine b) =>
        a.Reference.Value == b.Reference.Value
        && a.Name == b.Name
        && a.State == b.State
        && a.HasSnapshot == b.HasSnapshot
        && a.IpAddresses.SequenceEqual(b.IpAddresses);

    private async Task Bounded(Func<Task> action)
    {
        try
        {
            await action().WaitAsync(ShutdownTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Machine watcher cleanup on {Host} failed", _connection.Address);
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Until the signal completes, the bound passes or the watcher stops, whichever comes first.
    private static async Task WaitAsync(Task signal, TimeSpan bound, CancellationToken ct)
    {
        try
        {
            await signal.WaitAsync(bound, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    private static bool TryGetId(Dictionary<string, object> properties, out Guid id)
    {
        id = Guid.Empty;
        return properties.TryGetValue("config.uuid", out var value)
            && value is string uuid
            && Guid.TryParse(uuid, out id);
    }

    internal static bool IsSessionLost(Exception ex) => ex switch
    {
        FaultException<NotAuthenticated> => true,
        FaultException fault => fault.Message?.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) == true,
        CommunicationException => true,
        _ => false
    };

    internal static VsphereVirtualMachine ToMachine(
        ManagedObjectReference reference, Guid id, IReadOnlyDictionary<string, object> properties) => new()
        {
            Id = id,
            Name = properties.GetValueOrDefault("name") as string,
            Reference = reference,
            State = properties.GetValueOrDefault("summary.runtime.powerState") switch
            {
                VirtualMachinePowerState.poweredOn => "on",
                VirtualMachinePowerState.poweredOff => "off",
                VirtualMachinePowerState.suspended => "suspended",
                _ => "unknown"
            },
            IpAddresses = (properties.GetValueOrDefault("guest.net") as GuestNicInfo[] ?? [])
                .Where(x => x?.ipAddress != null).SelectMany(x => x.ipAddress).ToArray(),
            HasSnapshot = (properties.GetValueOrDefault("rootSnapshot") as ManagedObjectReference[])?.Length > 0
        };

    // Without the *Specified flags the serializer omits the values and vCenter waits with no bound.
    internal static WaitOptions BuildWaitOptions() => new()
    {
        maxWaitSeconds = MaxWaitSeconds,
        maxWaitSecondsSpecified = true,
        maxObjectUpdates = MaxObjectUpdates,
        maxObjectUpdatesSpecified = true
    };

    internal static PropertyFilterSpec BuildFilterSpec(ManagedObjectReference view) => new()
    {
        objectSet =
        [
            new ObjectSpec
            {
                obj = view,
                skip = true,
                skipSpecified = true,
                selectSet =
                [
                    new TraversalSpec
                    {
                        name = "traverseView",
                        type = "ContainerView",
                        path = "view",
                        skip = false,
                        skipSpecified = true
                    }
                ]
            }
        ],
        propSet =
        [
            new PropertySpec
            {
                type = "VirtualMachine",
                pathSet = WatchedPaths
            }
        ]
    };
}
