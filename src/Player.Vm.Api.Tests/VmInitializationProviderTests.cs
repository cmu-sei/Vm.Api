// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Services.HealthChecks;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Infrastructure.Options;
using Player.Vm.Api.Tests.Infrastructure;
using VimClient;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

public class VmInitializationProviderTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    [Theory]
    [InlineData(VirtualMachinePowerState.poweredOn, PowerState.On)]
    [InlineData(VirtualMachinePowerState.poweredOff, PowerState.Off)]
    [InlineData(VirtualMachinePowerState.suspended, PowerState.Suspended)]
    public async Task VsphereInitializesOnlyRequestedVmsAndEveryDiscoveryMapping(
        VirtualMachinePowerState reported, PowerState expected)
    {
        var vm = new VmEntity { Id = Guid.NewGuid(), Name = "new" };
        var unrelated = new VmEntity { Id = Guid.NewGuid(), Name = "unrelated", PowerState = PowerState.On };
        await Seed(vm, unrelated);
        using var services = BuildServices();
        using var service = BuildVsphere(services);
        var connection = AddConnection(service);
        var reference = Mor("vm-new");
        var unrelatedRef = Mor("vm-other");
        connection.MachineCache[unrelated.Id] = unrelatedRef;
        connection.VmGuids[unrelatedRef.Value] = unrelated.Id;
        service._machines[unrelated.Id] = connection.Address;
        connection.Client.FindByUuidAsync(Arg.Any<ManagedObjectReference>(), Arg.Is<ManagedObjectReference>(x => x == null), vm.Id.ToString(), true, false)
            .Returns(reference);
        connection.Client.RetrievePropertiesAsync(connection.Props, Arg.Any<PropertyFilterSpec[]>())
            .Returns(new RetrievePropertiesResponse([Properties(vm.Id, reference, reported)]));

        Assert.Contains(vm.Id, await service.InitializeVmsAsync([vm.Id], Ct));
        var aggregate = service.GetAggregate(vm.Id);
        Assert.Same(connection, aggregate.Connection);
        Assert.Same(reference, aggregate.MachineReference);
        Assert.Equal(vm.Id, service.GetVmIdByRef(reference.Value, connection.Address));
        Assert.Same(unrelatedRef, service.GetMachineById(unrelated.Id));
        Assert.Equal(unrelated.Id, service.GetVmIdByRef(unrelatedRef.Value, connection.Address));

        await using var check = NewContext();
        var updated = await check.Vms.SingleAsync(x => x.Id == vm.Id, Ct);
        Assert.Equal(expected, updated.PowerState);
        Assert.Equal(VmType.Vsphere, updated.Type);
        Assert.Empty(updated.IpAddresses); // guest.net is absent in the response.
        Assert.Equal(PowerState.On, (await check.Vms.SingleAsync(x => x.Id == unrelated.Id, Ct)).PowerState);
        await connection.Client.Received(1).RetrievePropertiesAsync(connection.Props,
            Arg.Is<PropertyFilterSpec[]>(filters =>
                filters.Length == 1 &&
                filters[0].objectSet.Length == 1 &&
                filters[0].objectSet[0].obj.Value == reference.Value &&
                filters[0].objectSet[0].selectSet == null &&
                filters[0].propSet.All(p => p.type == "VirtualMachine")));
    }

    [Fact]
    public async Task CachedReferencesAvoidUuidDiscoveryAndMissingVmDoesNotBlockOthers()
    {
        var found = new VmEntity { Id = Guid.NewGuid(), Name = "found" };
        var missing = new VmEntity { Id = Guid.NewGuid(), Name = "missing" };
        await Seed(found, missing);
        using var services = BuildServices();
        using var service = BuildVsphere(services);
        var connection = AddConnection(service);
        var reference = Mor("vm-cached");
        connection.MachineCache[found.Id] = reference;
        connection.Client.RetrievePropertiesAsync(connection.Props, Arg.Any<PropertyFilterSpec[]>())
            .Returns(new RetrievePropertiesResponse([Properties(found.Id, reference, VirtualMachinePowerState.poweredOn)]));

        var resolved = await service.InitializeVmsAsync([found.Id, missing.Id], Ct);
        Assert.Equal(found.Id, Assert.Single(resolved));
        await connection.Client.DidNotReceive().FindByUuidAsync(
            Arg.Any<ManagedObjectReference>(), Arg.Is<ManagedObjectReference>(x => x == null), found.Id.ToString(), true, false);
        await using var check = NewContext();
        Assert.Equal(PowerState.Unknown, (await check.Vms.SingleAsync(x => x.Id == missing.Id, Ct)).PowerState);
    }

    [Fact]
    public async Task FailedHostDoesNotDiscardHealthyHostsResults()
    {
        var vm = new VmEntity { Id = Guid.NewGuid(), Name = "new" };
        await Seed(vm);
        using var services = BuildServices();
        using var service = BuildVsphere(services);
        var bad = AddConnection(service, "bad");
        var good = AddConnection(service, "good");
        var reference = Mor("vm-found");
        foreach (var connection in new[] { bad, good })
            connection.MachineCache[vm.Id] = reference;
        bad.Client.RetrievePropertiesAsync(bad.Props, Arg.Any<PropertyFilterSpec[]>())
            .Returns(Task.FromException<RetrievePropertiesResponse>(new InvalidOperationException("offline")));
        good.Client.RetrievePropertiesAsync(good.Props, Arg.Any<PropertyFilterSpec[]>())
            .Returns(new RetrievePropertiesResponse([Properties(vm.Id, reference, VirtualMachinePowerState.poweredOn)]));

        Assert.Contains(vm.Id, await service.InitializeVmsAsync([vm.Id], Ct));
        Assert.Same(good, service.GetAggregate(vm.Id).Connection);
    }

    [Fact]
    public async Task DiscoveryHasAtMostEightCallsInFlightAcrossConnections()
    {
        using var services = BuildServices();
        using var service = BuildVsphere(services);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var calls = 0;
        var sync = new object();
        foreach (var connection in new[] { AddConnection(service, "one"), AddConnection(service, "two") })
        {
            connection.Client.FindByUuidAsync(Arg.Any<ManagedObjectReference>(), Arg.Is<ManagedObjectReference>(x => x == null), Arg.Any<string>(), true, false)
                .Returns(async _ =>
                {
                    lock (sync) { active++; calls++; peak = Math.Max(peak, active); }
                    try
                    {
                        await release.Task.WaitAsync(Ct);
                        return (ManagedObjectReference)null;
                    }
                    finally { lock (sync) active--; }
                });
        }
        var run = service.InitializeVmsAsync(Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToArray(), Ct);
        try
        {
            await PollLoop.Until(() => Volatile.Read(ref active) == 8, "eight concurrent discovery calls");
            Assert.Equal(8, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); }
        await run;
        Assert.Equal(8, peak);
        Assert.Equal(60, calls);
    }

    [Fact]
    public async Task IncrementalRefreshWaitsForFullInventoryAndSurvivesItsPruning()
    {
        var vm = new VmEntity { Id = Guid.NewGuid(), Name = "new" };
        await Seed(vm);
        using var services = BuildServices();
        using var service = BuildVsphere(services, ["vc"]);
        var connection = AddConnection(service);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource<RetrievePropertiesResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reference = Mor("vm-new");
        connection.Client.FindByUuidAsync(Arg.Any<ManagedObjectReference>(), Arg.Is<ManagedObjectReference>(x => x == null), vm.Id.ToString(), true, false)
            .Returns(reference);
        connection.Client.RetrievePropertiesAsync(connection.Props, Arg.Any<PropertyFilterSpec[]>())
            .Returns(call =>
            {
                var filters = call.ArgAt<PropertyFilterSpec[]>(1);
                if (filters[0].propSet.Length > 1)
                {
                    entered.TrySetResult();
                    return full.Task;
                }
                return Task.FromResult(new RetrievePropertiesResponse([Properties(vm.Id, reference, VirtualMachinePowerState.poweredOn)]));
            });
        await service.StartAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var initialize = service.InitializeVmsAsync([vm.Id], Ct);
            Assert.False(initialize.IsCompleted);
            await connection.Client.DidNotReceive().FindByUuidAsync(
                Arg.Any<ManagedObjectReference>(), Arg.Is<ManagedObjectReference>(x => x == null), vm.Id.ToString(), true, false);
            full.SetResult(new RetrievePropertiesResponse([]));
            Assert.Contains(vm.Id, await initialize.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            Assert.NotNull(service.GetAggregate(vm.Id));
            Assert.Equal(vm.Id, service.GetVmIdByRef(reference.Value, connection.Address));
        }
        finally
        {
            full.TrySetResult(new RetrievePropertiesResponse([]));
            await service.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task ProxmoxQueriesOnceAndUpdatesOnlyQueuedRowsIncludingCurrentNode()
    {
        var queued = ProxmoxVm(100);
        var other = ProxmoxVm(101);
        await Seed(queued, other);
        var cluster = new FakeProxmoxCluster();
        cluster.Has(100, status: "running", node: "new-node");
        cluster.Has(101, status: "running", node: "other-node");
        var resources = (await cluster.Service().GetVms()).ToArray();
        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetVms().Returns(Task.FromResult<IEnumerable<IClusterResourceVm>>(resources));
        using var services = BuildServices(proxmox);
        using var service = new ProxmoxStateService(NullLogger<ProxmoxStateService>.Instance,
            Monitor(new ProxmoxOptions { Enabled = true }), services);

        Assert.Contains(queued.Id, await service.InitializeVmsAsync([queued.Id], Ct));
        await proxmox.Received(1).GetVms();
        await using var check = NewContext();
        var rows = await check.Vms.ToDictionaryAsync(x => x.Id, Ct);
        Assert.Equal(PowerState.On, rows[queued.Id].PowerState);
        Assert.Equal("new-node", rows[queued.Id].ProxmoxVmInfo.Node);
        Assert.Equal(PowerState.Unknown, rows[other.Id].PowerState);
        Assert.Equal("old-node", rows[other.Id].ProxmoxVmInfo.Node);
    }

    [Fact]
    public async Task WorkerSkipsDeletedAndExternalRecordsAndOnlyRetriesUnresolvedEligibleIds()
    {
        var vm = new VmEntity { Id = Guid.NewGuid(), Name = "new" };
        var external = new VmEntity { Id = Guid.NewGuid(), Name = "external", Url = "https://console.example.test" };
        await Seed(vm, external);
        var connection = Substitute.For<IConnectionService>();
        connection.InitializeVmsAsync(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));
        using var services = BuildServices(connection: connection);
        var queue = new VmInitializationQueue(Monitor(new VmInitializationOptions()), TimeProvider.System);
        using var worker = new VmInitializationService(queue, services, NullLogger<VmInitializationService>.Instance);

        var unresolved = await worker.InitializeAsync([vm.Id, external.Id, Guid.NewGuid()], VmType.Vsphere, Ct);
        Assert.Equal(vm.Id, Assert.Single(unresolved));
        await connection.Received(1).InitializeVmsAsync(Arg.Is<Guid[]>(ids => ids.Length == 1 && ids[0] == vm.Id), Ct);
    }

    [Fact]
    public async Task SlowVsphereDoesNotBlockProxmoxAndShutdownCancelsInitialization()
    {
        var vsphereVm = new VmEntity { Id = Guid.NewGuid(), Name = "slow-vsphere" };
        var proxmoxVm = ProxmoxVm(100);
        await Seed(vsphereVm, proxmoxVm);
        var vsphere = Substitute.For<IConnectionService>();
        var proxmox = Substitute.For<IProxmoxStateService>();
        var vsphereEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxmoxEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vsphere.InitializeVmsAsync(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                vsphereEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
                return (IReadOnlySet<Guid>)new HashSet<Guid>();
            });
        proxmox.InitializeVmsAsync(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                proxmoxEntered.TrySetResult();
                return Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid> { proxmoxVm.Id });
            });

        var registrations = new ServiceCollection();
        registrations.AddScoped(_ => NewContext());
        registrations.AddSingleton(vsphere);
        registrations.AddSingleton(proxmox);
        using var services = registrations.BuildServiceProvider();
        var clock = new InitializationClock();
        var queue = new VmInitializationQueue(Monitor(new VmInitializationOptions()), clock);
        queue.Enqueue(vsphereVm);
        queue.Enqueue(proxmoxVm);
        clock.Advance(5);
        using var worker = new VmInitializationService(queue, services, NullLogger<VmInitializationService>.Instance);

        await worker.StartAsync(Ct);
        try
        {
            await Task.WhenAll(vsphereEntered.Task, proxmoxEntered.Task).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.False(worker.ExecuteTask.IsCompleted);
        }
        finally { await worker.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct); }
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ProxmoxInitializationCannotBeOverwrittenByAnOlderPeriodicSnapshot()
    {
        var vm = ProxmoxVm(100);
        await Seed(vm);
        var oldCluster = new FakeProxmoxCluster();
        oldCluster.Has(100, status: "stopped", node: "old-node");
        var newCluster = new FakeProxmoxCluster();
        newCluster.Has(100, status: "running", node: "new-node");
        var oldResources = await oldCluster.Service().GetVms();
        var newResources = await newCluster.Service().GetVms();
        var periodicEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var periodicResult = new TaskCompletionSource<IEnumerable<IClusterResourceVm>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetVms().Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                periodicEntered.TrySetResult();
                return periodicResult.Task;
            }
            return Task.FromResult(newResources);
        });
        using var services = BuildServices(proxmox);
        using var service = new ProxmoxStateService(NullLogger<ProxmoxStateService>.Instance,
            Monitor(new ProxmoxOptions { Enabled = true, StateRefreshIntervalSeconds = 60 }), services);
        await service.StartAsync(Ct);
        try
        {
            await periodicEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var initialize = service.InitializeVmsAsync([vm.Id], Ct);
            Assert.False(initialize.IsCompleted);
            Assert.Equal(1, calls);
            periodicResult.SetResult(oldResources);
            Assert.Contains(vm.Id, await initialize.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            await using var check = NewContext();
            var updated = await check.Vms.SingleAsync(x => x.Id == vm.Id, Ct);
            Assert.Equal(PowerState.On, updated.PowerState);
            Assert.Equal("new-node", updated.ProxmoxVmInfo.Node);
        }
        finally
        {
            periodicResult.TrySetResult(oldResources);
            await service.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task VsphereLargeBatchFetchesPropertiesOnceAndPreservesUnrelatedInventory()
    {
        using var services = BuildServices();
        using var service = BuildVsphere(services);
        var connection = AddConnection(service);
        var ids = Enumerable.Range(0, 250).Select(_ => Guid.NewGuid()).ToArray();
        var inventory = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).Concat(ids).ToArray();
        foreach (var id in inventory)
        {
            var reference = Mor(id.ToString());
            connection.MachineCache[id] = reference;
            connection.VmGuids[reference.Value] = id;
            service._machines[id] = connection.Address;
        }
        connection.Client.RetrievePropertiesAsync(connection.Props, Arg.Any<PropertyFilterSpec[]>())
            .Returns(call =>
            {
                var refs = call.ArgAt<PropertyFilterSpec[]>(1)[0].objectSet.Select(x => x.obj);
                return new RetrievePropertiesResponse(refs.Select(reference =>
                    Properties(Guid.Parse(reference.Value), reference, VirtualMachinePowerState.poweredOn)).ToArray());
            });
        var resolved = await service.InitializeVmsAsync(ids, Ct);
        Assert.Equal(250, resolved.Count);
        Assert.Equal(1250, connection.MachineCache.Count);
        Assert.Equal(1250, connection.VmGuids.Count);
        Assert.Equal(1250, service._machines.Count);
        await connection.Client.Received(1).RetrievePropertiesAsync(connection.Props,
            Arg.Is<PropertyFilterSpec[]>(filters => filters[0].objectSet.Length == 250));
        await connection.Client.DidNotReceiveWithAnyArgs().FindByUuidAsync(default, default, default, default, default);
    }

    private sealed class InitializationClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.Parse("2026-01-01T00:00:00Z").Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    private ServiceProvider BuildServices(IProxmoxService proxmox = null, IConnectionService connection = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        if (proxmox != null) services.AddSingleton(proxmox);
        if (connection != null) services.AddSingleton(connection);
        return services.BuildServiceProvider();
    }

    private static ConnectionService BuildVsphere(IServiceProvider services, string[] hosts = null)
        => new(Monitor(new VsphereOptions
        {
            Hosts = (hosts ?? []).Select(x => new VsphereHost { Address = x }).ToArray(),
            ConnectionRetryIntervalSeconds = 60,
            ConnectionTimeoutSeconds = 90,
            LoadCacheAfterMinutes = 5
        }), NullLogger<ConnectionService>.Instance, services, new ConnectionServiceHealthCheck());

    private static VsphereConnection AddConnection(ConnectionService service, string address = "vc")
    {
        var connection = new VsphereConnection(new VsphereHost { Address = address }, new VsphereOptions(), NullLogger.Instance)
        {
            Client = Substitute.For<IVimClient>(),
            Props = new ManagedObjectReference { type = "PropertyCollector", Value = "props" },
            Sic = new ServiceContent
            {
                rootFolder = new ManagedObjectReference { type = "Folder", Value = "root" },
                searchIndex = new ManagedObjectReference { type = "SearchIndex", Value = "search" }
            }
        };
        typeof(VsphereConnection).GetProperty(nameof(VsphereConnection.Connected)).SetValue(connection, true);
        service._connections[address] = connection;
        return connection;
    }

    private static IOptionsMonitor<T> Monitor<T>(T value) where T : class
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private static ManagedObjectReference Mor(string value) => new() { type = "VirtualMachine", Value = value };

    private static ObjectContent Properties(Guid id, ManagedObjectReference reference, VirtualMachinePowerState state)
        => new()
        {
            obj = reference,
            propSet = [
                new DynamicProperty { name = "config.uuid", val = id.ToString() },
                new DynamicProperty { name = "name", val = "machine" },
                new DynamicProperty { name = "summary.runtime.powerState", val = state }
            ]
        };

    private static VmEntity ProxmoxVm(int id)
    {
        var vm = new VmEntity { Id = Guid.NewGuid(), Name = $"pve-{id}", Type = VmType.Proxmox };
        vm.ProxmoxVmInfo = new ProxmoxVmInfo { VmId = vm.Id, Id = id, Node = "old-node" };
        return vm;
    }
}
