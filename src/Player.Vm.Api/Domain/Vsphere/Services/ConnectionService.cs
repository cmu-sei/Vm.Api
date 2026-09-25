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
using Player.Vm.Api.Domain.Services;

namespace Player.Vm.Api.Domain.Vsphere.Services;

public interface IConnectionService
{
    Task<IReadOnlySet<Guid>> InitializeVmsAsync(Guid[] ids, CancellationToken ct);
    ManagedObjectReference GetMachineById(Guid id);
    Guid? GetVmIdByRef(string reference, string vsphereHost);
    List<Network> GetNetworksByHost(string hostReference, string vsphereHost);
    Network GetNetworkByReference(string networkReference, string vsphereHost);
    Network GetNetworkByName(string networkName, string vsphereHost);
    Datastore GetDatastoreByName(string dsName, string vsphereHost);
    VsphereConnection GetConnection(string hostname);
    VsphereAggregate GetAggregate(Guid id);
    IEnumerable<VsphereConnection> GetAllConnections();
}

public class ConnectionService : BackgroundService, IConnectionService
{
    private readonly ILogger<ConnectionService> _logger;
    private readonly IOptionsMonitor<VsphereOptions> _optionsMonitor;
    private readonly IServiceProvider _serviceProvider;
    private AsyncAutoResetEvent _resetEvent = new(false);

    private readonly ConnectionServiceHealthCheck _connectionServiceHealthCheck;

    public ConcurrentDictionary<string, VsphereConnection> _connections = new(); // address to connection
    public ConcurrentDictionary<Guid, string> _machines = new(); // machine to vsphere address

    private readonly Dictionary<string, Task> _taskDict = new();
    private readonly SemaphoreSlim _discoverySlots = new(8, 8);

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

    private async Task DoWork(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        foreach (var host in options.Hosts)
        {
            var connection = _connections.GetOrAdd(host.Address, x => new VsphereConnection(host, options, _logger));
            if (!_taskDict.ContainsKey(connection.Address))
                _taskDict.Add(connection.Address, RefreshConnectionAsync(connection, host, options, cancellationToken));
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

    private async Task RefreshConnectionAsync(
        VsphereConnection connection, VsphereHost host, VsphereOptions options, CancellationToken ct)
    {
        await connection.InventoryGate.WaitAsync(ct);
        try
        {
            connection.Host = host;
            connection.Options = options;
            var machines = (await connection.Load()).ToArray();
            await PublishMachinesAsync(connection, machines, removeMissing: true, ct);
        }
        finally { connection.InventoryGate.Release(); }
    }

    public async Task<IReadOnlySet<Guid>> InitializeVmsAsync(Guid[] ids, CancellationToken ct)
    {
        // A failed connection is isolated from the other connections' discovery and writes.
        var resolved = new ConcurrentDictionary<Guid, byte>();
        await Task.WhenAll(_connections.Values.Select(async connection =>
        {
            await connection.InventoryGate.WaitAsync(ct);
            try
            {
                if (!connection.Enabled || !connection.Connected)
                    return;

                var candidates = ids.Where(id =>
                    !_machines.TryGetValue(id, out var address) || address == connection.Address).ToArray();
                if (candidates.Length == 0)
                    return;

                var machines = (await connection.LoadMachinesAsync(candidates, _discoverySlots, ct)).ToArray();
                await PublishMachinesAsync(connection, machines, removeMissing: false, ct);
                foreach (var vm in machines.Where(x => x.State != "unknown"))
                    resolved.TryAdd(vm.Id, 0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VM initialization failed on {Host}", connection.Address);
            }
            finally { connection.InventoryGate.Release(); }
        }));
        return resolved.Keys.ToHashSet();
    }

    // Both refresh paths hold the connection's gate until every cache and database update is done.
    private async Task PublishMachinesAsync(
        VsphereConnection connection, VsphereVirtualMachine[] machines, bool removeMissing, CancellationToken ct)
    {
        foreach (var machine in machines)
            _machines[machine.Id] = connection.Address;

        if (removeMissing)
        {
            foreach (var entry in _machines.Where(x => x.Value == connection.Address).ToArray())
                if (!connection.MachineCache.ContainsKey(entry.Key))
                    _machines.TryRemove(entry);
        }

        if (machines.Length == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<VmContext>();
        var ids = machines.Select(x => x.Id).ToArray();
        var vms = await dbContext.Vms.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var machine in machines)
        {
            if (!vms.TryGetValue(machine.Id, out var vm) ||
                VmInitializationQueue.GetProvider(vm) != VmType.Vsphere)
                continue;

            var state = machine.State switch
            {
                "on" => PowerState.On,
                "off" => PowerState.Off,
                "suspended" => PowerState.Suspended,
                _ => PowerState.Unknown
            };
            if (state != PowerState.Unknown)
                vm.PowerState = state;
            vm.IpAddresses = machine.IpAddresses;
            vm.Type = VmType.Vsphere;
            vm.HasSnapshot = machine.HasSnapshot;
        }
        await dbContext.SaveChangesAsync(ct);
    }

    public VsphereAggregate GetAggregate(Guid id)
    {
        string address;

        if (_machines.TryGetValue(id, out address))
        {
            VsphereConnection connection;

            if (_connections.TryGetValue(address, out connection))
            {
                ManagedObjectReference machineReference;
                if (connection.MachineCache.TryGetValue(id, out machineReference))
                {
                    return new VsphereAggregate(connection, machineReference);
                }
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
