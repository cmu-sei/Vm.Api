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
    private readonly ILogger _logger;

    // Property values by moref, as the change feed last reported them.
    private readonly Dictionary<string, Dictionary<string, object>> _objects = new();

    public VsphereMachineWatcher(
        VsphereConnection connection,
        Action<Guid> changed,
        Action requestReconnect,
        Func<TimeSpan> maxBackoff,
        ILogger logger)
    {
        _connection = connection;
        _changed = changed;
        _requestReconnect = requestReconnect;
        _maxBackoff = maxBackoff;
        _logger = logger;
    }

    internal TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan DisconnectedPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
            var session = _connection.GetSession();

            if (session.Client == null || session.Sic == null)
            {
                await Delay(DisconnectedPollInterval, ct);
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
                _connection.WatcherError = ex.Message;

                if (_connection.Generation != session.Generation)
                {
                    // The session was replaced under the pending call. The new one is ready now.
                    _logger.LogInformation("Session for {Host} was replaced. Rebuilding the machine watcher.", _connection.Address);
                    continue;
                }

                if (IsSessionLost(ex))
                {
                    _requestReconnect();
                }

                _logger.LogWarning(ex, "Machine watcher for {Host} failed. Retrying in {Delay}.", _connection.Address, backoff);
                await Delay(backoff, ct);

                var max = _maxBackoff();
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, Math.Max(max.Ticks, InitialBackoff.Ticks)));
            }
        }
    }

    private async Task WatchAsync(
        (IVimClient Client, ServiceContent Sic, int Generation) session, Action onHealthy, CancellationToken ct)
    {
        var client = session.Client;
        ManagedObjectReference view = null;
        ManagedObjectReference collector = null;

        try
        {
            view = (await client.CreateContainerViewAsync(
                session.Sic.viewManager, session.Sic.rootFolder, ["VirtualMachine"], true)).returnval;
            collector = await client.CreatePropertyCollectorAsync(session.Sic.propertyCollector);
            await client.CreateFilterAsync(collector, BuildFilterSpec(view), false);

            _objects.Clear();
            var seen = new HashSet<string>();
            var initial = true;
            var version = "";
            var options = BuildWaitOptions();

            while (_connection.Generation == session.Generation)
            {
                var update = await WaitAsync(client, collector, version, options, ct);

                if (update != null)
                {
                    version = update.version;
                    Apply(update, initial ? seen : null);

                    if (update.truncatedSpecified && update.truncated)
                    {
                        continue;
                    }
                }

                if (initial)
                {
                    Prune(seen);
                    initial = false;
                    _connection.WatcherError = null;
                    onHealthy();
                    _logger.LogInformation("Watching {Count} machines on {Host}", _objects.Count, _connection.Address);
                }
            }
        }
        finally
        {
            // Objects made under a session that has since been replaced went with it.
            if (_connection.Generation == session.Generation)
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

    private void Apply(UpdateSet update, HashSet<string> seen)
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
                    Apply(objectUpdate, seen);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error applying update for {Reference} on {Host}", objectUpdate.obj?.Value, _connection.Address);
                }
            }
        }
    }

    private void Apply(ObjectUpdate update, HashSet<string> seen)
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

        _connection.UpsertMachine(ToMachine(update.obj, id, properties));
        _changed(id);
    }

    // After the initial snapshot: forget machines this host had cached that the snapshot did not include.
    private void Prune(HashSet<string> seen)
    {
        foreach (var (id, machine) in _connection.MachineStates.ToArray())
        {
            if (!seen.Contains(machine.Reference.Value))
            {
                _connection.RemoveMachine(id, machine.Reference.Value);
            }
        }
    }

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
