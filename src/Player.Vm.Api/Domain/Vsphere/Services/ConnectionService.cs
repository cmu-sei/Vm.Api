// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VimClient;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Data;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Domain.Models;
using Nito.AsyncEx;
using Player.Vm.Api.Infrastructure.Extensions;
using Player.Vm.Api.Domain.Services.HealthChecks;
using System.Threading.Channels;

namespace Player.Vm.Api.Domain.Vsphere.Services;

public interface IConnectionService
{
    ManagedObjectReference GetMachineById(Guid id);
    Guid? GetVmIdByRef(string reference, string vsphereHost);
    List<Network> GetNetworksByHost(string hostReference, string vsphereHost);
    Network GetNetworkByReference(string networkReference, string vsphereHost);
    Network GetNetworkByName(string networkName, string vsphereHost);
    Datastore GetDatastoreByName(string dsName, string vsphereHost);
    VsphereConnection GetConnection(string hostname);
    VsphereAggregate GetAggregate(Guid id);
    IEnumerable<VsphereConnection> GetAllConnections();

    /// <summary>
    /// Copies the watcher's cached state for <paramref name="vm"/> onto it, if any vCenter has reported
    /// the machine. Returns false when none has.
    /// </summary>
    bool ApplyCachedState(Domain.Models.Vm vm);

    /// <summary>
    /// Queues <paramref name="id"/> for the persister, which writes its cached state to the database.
    /// </summary>
    void MarkDirty(Guid id);
}

public class ConnectionService : BackgroundService, IConnectionService
{
    private readonly ILogger<ConnectionService> _logger;
    private readonly IOptionsMonitor<VsphereOptions> _optionsMonitor;
    private readonly IServiceProvider _serviceProvider;
    private AsyncAutoResetEvent _resetEvent = new(false);

    private readonly ConnectionServiceHealthCheck _connectionServiceHealthCheck;

    public ConcurrentDictionary<string, VsphereConnection> _connections = new(); // address to connection

    private readonly Dictionary<string, Task> _taskDict = new();
    private readonly Dictionary<string, (CancellationTokenSource Cancel, Task Task)> _watchers = new();

    // Machine ids whose cached state has changed and not yet been written. One reader, so the
    // database only ever receives the latest cached value for a machine.
    private readonly Channel<Guid> _dirty = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, byte> _retried = new();

    internal TimeSpan PersistRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public ConnectionService(
            IOptionsMonitor<VsphereOptions> vsphereOptionsMonitor,
            ILogger<ConnectionService> logger,
            IServiceProvider serviceProvider,
            ConnectionServiceHealthCheck connectionServiceHealthCheck
        )
    {
        _optionsMonitor = vsphereOptionsMonitor;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _connectionServiceHealthCheck = connectionServiceHealthCheck;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        var persister = PersistAsync(cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await DoWork(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception in ConnectionService");
                }

                await _resetEvent.WaitAsync(TimeSpan.FromSeconds(_optionsMonitor.CurrentValue.ConnectionRetryIntervalSeconds), cancellationToken);
            }
        }
        finally
        {
            await Task.WhenAll(_watchers.Keys.ToArray().Select(StopWatcherAsync));
            await Task.WhenAll(_connections.Values.Select(x => x.LogoutAsync().WaitAsync(ShutdownTimeout).ContinueWith(_ => { })));
            await persister.ContinueWith(_ => { });
        }
    }

    private async Task DoWork(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var hosts = options.Hosts ?? [];

        foreach (var address in _connections.Keys.Except(hosts.Select(x => x.Address)).ToArray())
        {
            await RemoveConnectionAsync(address);
        }

        foreach (var host in hosts)
        {
            var connection = _connections.GetOrAdd(host.Address, x => new VsphereConnection(host, options, _logger));
            connection.Options = options;
            connection.Host = host;

            if (!_taskDict.ContainsKey(connection.Address))
            {
                _taskDict.Add(connection.Address, connection.Load());
            }

            if (host.Enabled)
            {
                StartWatcher(connection, cancellationToken);
            }
            else if (_watchers.ContainsKey(host.Address))
            {
                await StopWatcherAsync(host.Address);
                connection.ClearMachines();
                await connection.LogoutAsync().WaitAsync(ShutdownTimeout).ContinueWith(_ => { });
            }
        }

        var allTasks = Task.WhenAll(_taskDict.Values);
        await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(options.ConnectionTimeoutSeconds), cancellationToken));
        _connectionServiceHealthCheck.StartupCheckComplete = true;
        _connectionServiceHealthCheck.Connections = _connections.Values.ToArray();

        foreach (var (host, task) in _taskDict.ToArray())
        {
            if (!task.IsCompleted)
            {
                _logger.LogWarning("Loading connection for {Host} did not complete in time. It will be checked again in the next loop.", host);
                continue;
            }
            if (task.IsFaulted)
                _logger.LogWarning(task.Exception, "Loading connection for {Host} failed", host);
            _taskDict.Remove(host);
        }
    }

    private void StartWatcher(VsphereConnection connection, CancellationToken cancellationToken)
    {
        if (_watchers.ContainsKey(connection.Address))
            return;

        var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = new VsphereMachineWatcher(
            connection,
            MarkDirty,
            () => _resetEvent.Set(),
            () => TimeSpan.FromSeconds(_optionsMonitor.CurrentValue.ConnectionRetryIntervalSeconds),
            _logger);
        _watchers[connection.Address] = (cancel, Task.Run(() => watcher.RunAsync(cancel.Token)));
    }

    private async Task StopWatcherAsync(string address)
    {
        if (!_watchers.Remove(address, out var watcher))
            return;

        watcher.Cancel.Cancel();

        try
        {
            await watcher.Task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Machine watcher for {Host} ended with an error", address);
        }
        finally
        {
            watcher.Cancel.Dispose();
        }
    }

    private async Task RemoveConnectionAsync(string address)
    {
        await StopWatcherAsync(address);

        if (_connections.TryRemove(address, out var connection))
        {
            _logger.LogInformation("vSphere host {Host} was removed from configuration", address);
            connection.ClearMachines();
            await connection.LogoutAsync().WaitAsync(ShutdownTimeout).ContinueWith(_ => { });
        }
    }

    public void MarkDirty(Guid id)
    {
        _dirty.Writer.TryWrite(id);
    }

    public bool ApplyCachedState(Domain.Models.Vm vm)
    {
        var machine = GetCachedMachine(vm.Id);

        if (machine == null)
            return false;

        ApplyState(vm, machine);
        return true;
    }

    private VsphereVirtualMachine GetCachedMachine(Guid id)
    {
        foreach (var connection in _connections.Values)
        {
            if (connection.Enabled && connection.MachineStates.TryGetValue(id, out var machine))
                return machine;
        }

        return null;
    }

    private static void ApplyState(Domain.Models.Vm vm, VsphereVirtualMachine machine)
    {
        var state = machine.State switch
        {
            "on" => PowerState.On,
            "off" => PowerState.Off,
            "suspended" => PowerState.Suspended,
            _ => PowerState.Unknown
        };

        // A machine whose power state could not be read keeps the one it had.
        if (state != PowerState.Unknown)
            vm.PowerState = state;

        vm.IpAddresses = machine.IpAddresses;
        vm.Type = VmType.Vsphere;
        vm.HasSnapshot = machine.HasSnapshot;
    }

    // Drains whatever is queued in one batch, with no debounce: during a burst the next batch
    // simply collects the ids that arrived while this one was being written.
    private async Task PersistAsync(CancellationToken ct)
    {
        try
        {
            while (await _dirty.Reader.WaitToReadAsync(ct))
            {
                var ids = new HashSet<Guid>();
                while (_dirty.Reader.TryRead(out var id))
                    ids.Add(id);

                try
                {
                    await PersistBatchAsync(ids, ct);

                    foreach (var id in ids)
                        _retried.TryRemove(id, out _);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save the state of {Count} vSphere machines", ids.Count);
                    Retry(ids, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    internal async Task PersistBatchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var machines = ids
            .Select(GetCachedMachine)
            .Where(x => x != null)
            .ToDictionary(x => x.Id);

        if (machines.Count == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<VmContext>();
        var keys = machines.Keys.ToArray();
        var vms = await dbContext.Vms.Where(x => keys.Contains(x.Id)).ToListAsync(ct);

        foreach (var vm in vms)
        {
            // Re-read so a change cached while the query ran is the one written.
            ApplyState(vm, GetCachedMachine(vm.Id) ?? machines[vm.Id]);
        }

        await dbContext.SaveChangesAsync(ct);
    }

    // One more attempt per id after a short delay, then give up until the machine next changes.
    private void Retry(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var retry = ids.Where(id => _retried.TryAdd(id, 0)).ToArray();

        foreach (var id in ids.Except(retry))
        {
            _retried.TryRemove(id, out _);
            _logger.LogWarning("Giving up on saving the state of vSphere machine {Id} until it next changes", id);
        }

        if (retry.Length == 0)
            return;

        _ = RequeueAsync();

        async Task RequeueAsync()
        {
            try
            {
                await Task.Delay(PersistRetryDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var id in retry)
                MarkDirty(id);
        }
    }

    public VsphereAggregate GetAggregate(Guid id)
    {
        foreach (var connection in _connections.Values)
        {
            if (connection.MachineCache.TryGetValue(id, out var machineReference))
            {
                return new VsphereAggregate(connection, machineReference);
            }
        }

        return null;
    }

    public IEnumerable<VsphereConnection> GetAllConnections()
    {
        return _connections.Values;
    }

    public VsphereConnection GetConnection(string hostname)
    {
        VsphereConnection connection = null;
        _connections.TryGetValue(hostname, out connection);
        return connection;
    }

    public ManagedObjectReference GetMachineById(Guid id)
    {
        ManagedObjectReference machineReference;
        foreach (var connection in _connections.Values)
        {
            if (connection.MachineCache.TryGetValue(id, out machineReference))
            {
                return machineReference;
            }
        }

        return null;
    }

    public Guid? GetVmIdByRef(string reference, string vsphereHost)
    {
        VsphereConnection connection;
        if (_connections.TryGetValue(vsphereHost, out connection))
        {
            Guid id;
            if (connection.VmGuids.TryGetValue(reference, out id))
            {
                return id;
            }
        }

        return null;
    }

    public List<Network> GetNetworksByHost(string hostReference, string vsphereHost)
    {
        VsphereConnection connection;
        List<Network> networks = new List<Network>();

        if (_connections.TryGetValue(vsphereHost, out connection))
        {
            connection.NetworkCache.TryGetValue(hostReference, out networks);
        }

        return networks;
    }

    public Network GetNetworkByReference(string networkReference, string vsphereHost)
    {
        VsphereConnection connection;
        Network network = null;

        if (_connections.TryGetValue(vsphereHost, out connection))
        {
            network = connection.NetworkCache.Values.SelectMany(x => x).Where(n => n.Reference == networkReference).FirstOrDefault();
        }

        return network;
    }

    public Network GetNetworkByName(string networkName, string vsphereHost)
    {
        VsphereConnection connection;
        Network network = null;

        if (_connections.TryGetValue(vsphereHost, out connection))
        {
            network = connection.NetworkCache.Values.SelectMany(x => x).Where(n => n.Name == networkName).FirstOrDefault();
        }

        return network;
    }

    public Datastore GetDatastoreByName(string dsName, string vsphereHost)
    {
        VsphereConnection connection;
        Datastore datastore = null;

        if (_connections.TryGetValue(vsphereHost, out connection))
        {
            connection.DatastoreCache.TryGetValue(dsName, out datastore);
        }

        return datastore;
    }
}
