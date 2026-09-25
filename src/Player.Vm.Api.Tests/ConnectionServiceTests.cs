// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Crucible.Common.EntityEvents.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Services.HealthChecks;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Features.Vms.EventHandlers;
using Player.Vm.Api.Tests.Infrastructure;
using VimClient;
using Xunit;
using static Player.Vm.Api.Tests.Infrastructure.FakeChangeFeed;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// vSphere's <c>ConnectionService</c> and <c>VsphereConnection</c>: the session each vCenter host keeps,
/// the machine watcher each enabled host runs, and the persister that writes what the watchers report.
/// </summary>
/// <remarks>
/// <para>
/// Every vCenter here is a <see cref="FakeChangeFeed"/>, reached through
/// <see cref="VsphereConnection.ClientFactory"/> on a connection seeded into the service before it starts.
/// The persister writes through a real per-scope <see cref="VmContext"/>, as production's does.
/// </para>
/// <para>
/// The tests that run the whole service let its connection loop turn on its own, at the smallest interval
/// the options allow - a second - because the loop has no public nudge. They are the slow ones.
/// </para>
/// </remarks>
public class ConnectionServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private const string Address = "vcenter";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private readonly FakeChangeFeed _feed = new();
    private readonly IOptionsMonitor<VsphereOptions> _options = Substitute.For<IOptionsMonitor<VsphereOptions>>();
    private readonly ConnectionServiceHealthCheck _health = new();
    private int _contextsRefused;

    #region Connect

    [Fact]
    public async Task Load_WhileTheSessionIsValid_KeepsIt()
    {
        var logins = 0;
        var connection = Connection(_ => { logins++; return _feed.Client; });

        await connection.Load();
        var first = connection.Current;
        await connection.Load();
        await connection.Load();

        Assert.True(connection.Connected);
        Assert.Equal(1, logins);
        Assert.Same(first, connection.Current);
        await _feed.Client.DidNotReceive().LogoutAsync(Arg.Any<ManagedObjectReference>());
    }

    [Fact]
    public async Task Load_WhenTheSessionHasExpired_LogsInAgainAndLogsTheOldOneOut()
    {
        var replacement = new FakeChangeFeed();
        var clients = new Queue<IVimClient>([_feed.Client, replacement.Client]);
        var connection = Connection(_ => clients.Dequeue());

        await connection.Load();
        _feed.SessionValid = false;
        await connection.Load();

        Assert.True(connection.Connected);
        Assert.Same(replacement.Client, connection.Client);
        await PollLoop.Until(() => Calls(_feed, nameof(IVimClient.LogoutAsync)) == 1, "the old session to be logged out");
    }

    [Fact]
    public async Task Load_WhenLoginFails_IsDisconnected()
    {
        var failing = new FakeChangeFeed();
        failing.Client.LoginAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException<UserSession>(new Exception("bad password")));
        var connection = Connection(_ => failing.Client);

        await connection.Load();

        Assert.False(connection.Connected);
        Assert.Null(connection.Client);
    }

    #endregion

    #region VM creation

    /// <summary>
    /// The race the post-commit handler closes: vCenter reports the machine before its row exists, so
    /// the persister has nothing to write then, and no later change will prompt it. The row is created
    /// Unknown, and the handler's queueing is what gets the cached state written.
    /// </summary>
    [Fact]
    public async Task CreatingAVmVcenterHasAlreadyReported_WritesItsStateAfterCommit()
    {
        _feed.Then(Page("1", Enter("vm-1", A, VirtualMachinePowerState.poweredOn, ["10.0.0.5"])));
        var (service, _) = Running();

        await service.StartAsync(Ct);
        try
        {
            await PollLoop.Until(() => _feed.Idle, "the watcher to report the machine");

            var player = Substitute.For<IPlayerService>();
            player.CanManageTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test"));
            var vms = new VmService(Db, player, principal, TestMapper.Value, Substitute.For<INetworkService>());

            var created = await vms.CreateAsync(new VmCreateForm { Id = A, Name = "new", TeamIds = [Guid.NewGuid()] }, Ct);
            Assert.Equal(PowerState.Unknown, created.PowerState);

            await new VmCreatedInitializationHandler(service).Handle(new EntityCreated<VmEntity>(await Read(A)), Ct);

            await UntilWritten(A, PowerState.On);
            var row = await Read(A);
            Assert.Equal(["10.0.0.5"], row.IpAddresses);
            Assert.Equal(VmType.Vsphere, row.Type);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    #endregion

    #region Persister

    /// <summary>
    /// The persister writes whatever is cached when it runs, not what was cached when the id was queued,
    /// so a batch can never write a state older than one the watcher has already reported. Rows with a
    /// custom console URL are written too: the cache, not the URL, decides that a Vm is on vSphere.
    /// </summary>
    [Fact]
    public async Task PersistBatch_WritesTheLatestCachedState()
    {
        await Seed(
            new VmEntity { Id = A, Name = "a", Url = "https://console.example/a" },
            new VmEntity { Id = B, Name = "b", PowerState = PowerState.On },
            new VmEntity { Id = C, Name = "c", PowerState = PowerState.On });
        var service = Service();
        var connection = Cache(service, Machine(A, "off"));
        connection.UpsertMachine(Machine(A, "on", ["10.0.0.5"]));
        connection.UpsertMachine(Machine(C, "unknown", ["10.0.0.9"]));

        await service.PersistBatchAsync([A, B, C], Ct);

        var a = await Read(A);
        Assert.Equal(PowerState.On, a.PowerState);
        Assert.Equal(["10.0.0.5"], a.IpAddresses);
        Assert.Equal(VmType.Vsphere, a.Type);
        Assert.Equal("https://console.example/a", a.Url);

        Assert.Equal(PowerState.On, (await Read(B)).PowerState);
        Assert.Equal(VmType.Unknown, (await Read(B)).Type);

        var c = await Read(C);
        Assert.Equal(PowerState.On, c.PowerState);
        Assert.Equal(["10.0.0.9"], c.IpAddresses);
    }

    [Fact]
    public async Task PersistBatch_IgnoresADisabledHost()
    {
        await Seed(new VmEntity { Id = A, Name = "a", PowerState = PowerState.Off });
        var service = Service();
        Cache(service, Machine(A, "on"), enabled: false);

        await service.PersistBatchAsync([A], Ct);

        var a = await Read(A);
        Assert.Equal(PowerState.Off, a.PowerState);
        Assert.Equal(VmType.Unknown, a.Type);
    }

    #endregion

    #region The running service

    /// <summary>
    /// End to end: the connection loop logs in, starts the host's watcher, the watcher reports the
    /// machine and the persister writes it.
    /// </summary>
    [Fact]
    public async Task RunningService_WritesWhatTheWatcherReports()
    {
        await Seed(new VmEntity { Id = A, Name = "a" });
        _feed.Then(Page("1", Enter("vm-1", A, VirtualMachinePowerState.poweredOn, ["10.0.0.5"])));
        var (service, _) = Running();

        await service.StartAsync(Ct);
        try
        {
            await UntilWritten(A, PowerState.On);
            Assert.Same(_feed.Client, service.GetConnection(Address).Client);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, Calls(_feed, nameof(IVimClient.LogoutAsync)));
        Assert.Equal(1, Calls(_feed, nameof(IVimClient.DestroyPropertyCollectorAsync)));
    }

    [Fact]
    public async Task RunningService_RetriesAFailedWriteOnce()
    {
        await Seed(new VmEntity { Id = A, Name = "a" });
        _feed.Then(Page("1", Enter("vm-1", A, VirtualMachinePowerState.poweredOn)));
        var (service, _) = Running(refuseContexts: 1);

        await service.StartAsync(Ct);
        try
        {
            await UntilWritten(A, PowerState.On);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, _contextsRefused);
    }

    /// <summary>
    /// A watcher only returns once cancelled, so a task that ended any other way is one that died. The
    /// only way out of its catch is the log call, so a logger that throws once kills it.
    /// </summary>
    [Fact]
    public async Task AWatcherThatDied_IsRestartedByTheNextLoop()
    {
        await Seed(new VmEntity { Id = A, Name = "a" });
        _feed
            .ThenThrow(new InvalidOperationException("boom"))
            .Then(Page("1", Enter("vm-1", A, VirtualMachinePowerState.poweredOn)));
        var logger = new ThrowOnceLogger("Machine watcher for");
        var (service, _) = Running(logger: logger);

        await service.StartAsync(Ct);
        try
        {
            await UntilWritten(A, PowerState.On);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(logger.Thrown);
        Assert.Equal(2, _feed.ViewsCreated);
    }

    [Fact]
    public async Task DisablingAHost_StopsItsWatcherClearsItsCachesAndLogsOut()
    {
        await Seed(new VmEntity { Id = A, Name = "a" });
        _feed.Then(Page("1", Enter("vm-1", A, VirtualMachinePowerState.poweredOn)));
        var (service, connection) = Running();

        await service.StartAsync(Ct);
        try
        {
            await UntilWritten(A, PowerState.On);

            _options.CurrentValue.Returns(Options(new VsphereHost { Address = Address, Enabled = false }));

            await PollLoop.Until(() => Calls(_feed, nameof(IVimClient.LogoutAsync)) == 1, "the disabled host to log out");
            Assert.Empty(connection.MachineStates);
            Assert.Null(connection.Client);
            Assert.Equal(1, Calls(_feed, nameof(IVimClient.CancelWaitForUpdatesAsync)));
            Assert.Equal(1, Calls(_feed, nameof(IVimClient.DestroyPropertyCollectorAsync)));
            Assert.Same(connection, service.GetConnection(Address));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RemovingAHost_DropsItsConnection()
    {
        _feed.Then(Page("1", Enter("vm-1", A, VirtualMachinePowerState.poweredOn)));
        var (service, connection) = Running();

        await service.StartAsync(Ct);
        try
        {
            await PollLoop.Until(() => _feed.Idle, "the watcher to take the snapshot");

            _options.CurrentValue.Returns(Options());

            await PollLoop.Until(() => service.GetConnection(Address) == null, "the removed host to be dropped");
            await PollLoop.Until(() => Calls(_feed, nameof(IVimClient.LogoutAsync)) == 1, "the removed host to log out");
            Assert.Empty(connection.MachineStates);
            Assert.Null(service.GetMachineById(A));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    #endregion

    #region Health

    [Fact]
    public async Task Health_WithAWatcherError_IsDegraded()
    {
        var connection = Connection(_ => _feed.Client);
        await connection.Load();
        connection.WatcherError = "boom";
        var health = new ConnectionServiceHealthCheck { StartupCheckComplete = true, Connections = [connection] };

        var result = await health.CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("machine updates", result.Description);
    }

    #endregion

    private static VsphereOptions Options(params VsphereHost[] hosts) => new()
    {
        Hosts = hosts,
        ConnectionRetryIntervalSeconds = 1,
        ConnectionTimeoutSeconds = 10,
        LoadCacheAfterMinutes = 60
    };

    private static VsphereHost Host(bool enabled = true) =>
        new() { Address = Address, Username = "user", Password = "password", Enabled = enabled };

    private static VsphereConnection Connection(Func<VsphereHost, IVimClient> factory, bool enabled = true) =>
        new(Host(enabled), Options(), NullLogger.Instance) { ClientFactory = factory };

    private ConnectionService Service(IServiceProvider provider = null, ILogger<ConnectionService> logger = null) =>
        new(_options, logger ?? NullLogger<ConnectionService>.Instance, provider ?? Provider(), _health)
        {
            PersistRetryDelay = TimeSpan.FromMilliseconds(10)
        };

    // A fresh context per scope, as production registers it. The first refuseContexts are refused, to
    // fail a write.
    private IServiceProvider Provider(int refuseContexts = 0)
    {
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            if (Interlocked.Increment(ref attempts) > refuseContexts)
                return NewContext();

            Interlocked.Increment(ref _contextsRefused);
            throw new InvalidOperationException("Refused for the test");
        });
        return services.BuildServiceProvider();
    }

    private static VsphereConnection Cache(ConnectionService service, VsphereVirtualMachine machine, bool enabled = true)
    {
        var connection = service._connections.GetOrAdd(Address, _ => Connection(_ => throw new InvalidOperationException(), enabled));
        connection.UpsertMachine(machine);
        return connection;
    }

    // A service whose one configured host is this test's fake vCenter.
    private (ConnectionService Service, VsphereConnection Connection) Running(
        int refuseContexts = 0, ILogger<ConnectionService> logger = null)
    {
        var host = Host();
        _options.CurrentValue.Returns(Options(host));
        var service = Service(Provider(refuseContexts), logger);
        var connection = new VsphereConnection(host, Options(host), NullLogger.Instance) { ClientFactory = _ => _feed.Client };
        service._connections[Address] = connection;
        return (service, connection);
    }

    private static VsphereVirtualMachine Machine(Guid id, string state, string[] ips = null, bool snapshot = false) => new()
    {
        Id = id,
        Reference = Mor("VirtualMachine", $"vm-{id.ToString()[..4]}"),
        State = state,
        IpAddresses = ips ?? [],
        HasSnapshot = snapshot
    };

    private async Task<VmEntity> Read(Guid id)
    {
        await using var context = NewContext();
        return await context.Vms.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
    }

    private async Task UntilWritten(Guid id, PowerState state)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while ((await Read(id)).PowerState != state)
        {
            if (clock.Elapsed.TotalSeconds > 10)
                Assert.Fail($"Waited 10s for {id} to be written as {state}.");

            await Task.Delay(20, Ct);
        }
    }

    private static int Calls(FakeChangeFeed feed, string method) =>
        feed.Client.ReceivedCalls().Count(x => x.GetMethodInfo().Name == method);

    // Throws from the first log call whose message starts with the prefix.
    private sealed class ThrowOnceLogger(string prefix) : ILogger<ConnectionService>
    {
        private int _thrown;

        public bool Thrown => _thrown == 1;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (formatter(state, exception).StartsWith(prefix) && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new InvalidOperationException("Logger failed for the test");
        }
    }
}
