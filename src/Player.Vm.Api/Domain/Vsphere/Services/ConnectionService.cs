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

    private Dictionary<string, Task<IEnumerable<VsphereVirtualMachine>>> _taskDict = new();

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
                await _resetEvent.WaitAsync(TimeSpan.FromSeconds(_optionsMonitor.CurrentValue.ConnectionRetryIntervalSeconds), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in ConnectionService");
            }
        }
    }

    private async Task DoWork(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;

        foreach (var host in options.Hosts)
        {
            var connection = _connections.GetOrAdd(host.Address, x => new VsphereConnection(host, options, _logger));
            connection.Options = options;
            connection.Host = host;

            if (!_taskDict.ContainsKey(connection.Address))
            {
                _taskDict.Add(connection.Address, connection.Load());
            }
        }

        var timeout = Task.Delay(TimeSpan.FromSeconds(options.ConnectionTimeoutSeconds), cancellationToken);
        var allTasks = Task.WhenAll(_taskDict.Values);
        var completed = await Task.WhenAny(allTasks, timeout);
        var completedTasks = _taskDict.Where(x => x.Value.IsCompletedSuccessfully).ToDictionary();
        _connectionServiceHealthCheck.StartupCheckComplete = true;
        _connectionServiceHealthCheck.Connections = _connections.Select(x => x.Value).ToArray();

        var results = new List<VsphereVirtualMachine>();

        foreach (var (key, task) in _taskDict.ToList())
        {
            if (!task.IsCompleted)
            {
                _logger.LogWarning(task.Exception, "Loading connection for {Host} did not complete in time. It will be checked again in the next loop.", key);
                continue;
            }

            if (task.IsCompletedSuccessfully)
            {
                results.AddRange(task.Result);
            }
            else if (task.IsFaulted)
            {
                _logger.LogWarning(task.Exception, "Loading connection for {Host} failed", key);
            }
            else if (task.IsCanceled)
            {
                _logger.LogInformation("Loading connection for {Host} was canceled", key);
            }

            _taskDict.Remove(key);
        }

        this.ProcessTasks(completedTasks);
        await this.UpdateVms(results);
    }

    private void ProcessTasks(Dictionary<string, Task<IEnumerable<VsphereVirtualMachine>>> taskDict)
    {
        // Add or update machines cache
        foreach (var kvp in taskDict)
        {
            var machines = kvp.Value.Result;

            foreach (var machine in machines)
            {
                _machines.AddOrUpdate(machine.Id, kvp.Key, (k, v) => v = kvp.Key);
            }
        }

        // Remove machines that no longer exist from cache
        var allMachines = _connections.Values.SelectMany(x => x.MachineCache.Select(y => y.Key));

        foreach (var kvp in _machines)
        {
            if (!allMachines.Contains(kvp.Key))
            {
                _machines.TryRemove(kvp);
            }
        }
    }

    private async Task UpdateVms(IEnumerable<VsphereVirtualMachine> vsphereVirtualMachines)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<VmContext>();
            var vms = await dbContext.Vms.ToArrayAsync();

            foreach (var vsphereVirtualMachine in vsphereVirtualMachines)
            {
                var vm = vms.FirstOrDefault(x => x.Id == vsphereVirtualMachine.Id);

                if (vm != null)
                {
                    var powerState = vsphereVirtualMachine.State == "on" ? PowerState.On : PowerState.Off;
                    vm.PowerState = powerState;
                    vm.IpAddresses = vsphereVirtualMachine.IpAddresses;
                    vm.Type = VmType.Vsphere;
                    vm.HasSnapshot = vsphereVirtualMachine.HasSnapshot;
                }
            }

            var count = await dbContext.SaveChangesAsync();
        }
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