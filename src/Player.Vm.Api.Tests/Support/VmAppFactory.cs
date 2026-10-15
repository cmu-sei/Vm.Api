// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// The per-class factory variant: vm.api's tests arrange and assert on the NSubstitute doubles below (the
// hypervisor services, the player.api clients, the task pollers), so one host serves one test class.
// Hub contexts stay real: HubConnectionTests connects real SignalR clients to the hosted hubs, and the
// handlers that broadcast are tested against HubRecorders of their own. Program.Main runs
// InitializeDatabase with no switch to skip it, so the host gets a pair of throwaway databases of its own.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using NSubstitute.ClearExtensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Player.Api.Client;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// Hosts <c>Player.Vm.Api</c> in process, once per test class, with the collaborators that leave the
/// process substituted. Everything between the HTTP request and those collaborators is the production
/// wiring: routing, model binding, the authorization policies, the MediatR pipeline behaviors, the
/// handlers, AutoMapper and EF Core against real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Substituted, and why:
/// <list type="bullet">
/// <item><see cref="Vsphere"/> and <see cref="Proxmox"/>: the hypervisors. Unit tests prove the services
/// build the right per-Vm error dictionary; these prove it survives the handler, the serializer and the
/// wire.</item>
/// <item><see cref="PlayerApiClient"/>: the generated player.api client, answered by <see cref="Player"/>
/// for the caller of the request in flight. The application's own <c>PlayerService</c>, which turns those
/// answers into every authorization and visibility decision, is real.</item>
/// <item><see cref="Views"/>: <c>ViewService</c>, player.api by its admin client with a cache of its own.
/// Its team-to-View lookup is answered by <see cref="Player"/>; the entity-event handlers' View lookups are
/// arranged per test. <c>ViewServiceTests</c> covers the real one at the socket.</item>
/// <item><see cref="Callbacks"/>: the webhook send queue, which processes on a thread-pool thread outside
/// any request and would reach the host's own database.</item>
/// <item><see cref="IsoProvider"/>: ISO storage on a hypervisor. <c>IsoService</c> stays real.</item>
/// <item><see cref="VsphereTasks"/> and <see cref="ProxmoxTasks"/>: the task pollers the CheckTasks
/// pipeline behaviors poke after every power command.</item>
/// </list>
/// Removed: every <c>IHostedService</c>, each driven directly by its own test. Replaced: token validation
/// (<see cref="TestAuthHandler"/>), <c>IHttpClientFactory</c> (<see cref="OutboundHttp"/>), and the
/// <see cref="VmContext"/> and <see cref="VmLoggingContext"/> registrations, so each request reaches the
/// databases of the test that sent it.
/// </para>
/// <para>
/// A substitute is safe here because one class's tests run one after another, so no other class's
/// request can reach it. <c>ApiTestBase</c> resets the player.api side for each test
/// (<see cref="ResetPlayer"/>); each class clears the rest of what it arranges in its own
/// <c>InitializeAsync</c>.
/// </para>
/// </remarks>
public class VmAppFactory(DatabaseFixture database) : WebApplicationFactory<Startup>, ITestHttpHost, IAsyncLifetime
{
    /// <summary>The scopes every authenticated test request carries, as <c>appsettings.json</c> ships them.</summary>
    public const string AuthorizationScope = "player player-vm";

    /// <summary>The ISO size ceiling this host runs with. See <see cref="TestConfiguration"/>.</summary>
    public const long MaxIsoFileSize = TestConfiguration.MaxIsoFileSize;

    /// <summary>The Proxmox cluster this host is configured against. See <see cref="TestConfiguration"/>.</summary>
    public const string ProxmoxHost = TestConfiguration.ProxmoxHost;

    private ITestDatabaseSession<VmContext> _hostSession;

    /// <summary>
    /// Whether this host runs with <c>VmUsageLogging:Enabled</c>. False, as <c>appsettings.json</c> ships
    /// it. A host setting rather than something a test can flip: <c>Startup</c> reads it once to choose
    /// the <c>IVmUsageLoggingService</c>, so the other side needs a host of its own
    /// (<see cref="VmUsageLoggingEnabledFactory"/>).
    /// </summary>
    protected virtual bool VmUsageLoggingEnabled => false;

    public IVsphereService Vsphere { get; } = Substitute.For<IVsphereService>();
    public IProxmoxService Proxmox { get; } = Substitute.For<IProxmoxService>();

    /// <summary>
    /// The generated player.api client, the one thing of player.api's that leaves the process. Answered by
    /// <see cref="Player"/>; the application's own <c>PlayerService</c> above it is real.
    /// </summary>
    public IPlayerApiClient PlayerApiClient { get; } = Substitute.For<IPlayerApiClient>();

    /// <summary>
    /// Resolves a team to the View it belongs to. Reached by the SignalR entity-event handlers on every Vm
    /// write; unstubbed it answers null, which those handlers treat as "no View group to notify".
    /// </summary>
    public IViewService Views { get; } = Substitute.For<IViewService>();

    /// <summary>
    /// What player.api knows: the teams' Views and each caller's permissions. Answers
    /// <see cref="PlayerApiClient"/> for the user of the request in flight, and <see cref="Views"/>' team
    /// lookups. Tests write to it through <c>Actor()</c>; <c>ApiTestBase</c> resets it for each test.
    /// </summary>
    public PlayerDirectory Player { get; private set; }

    /// <summary>
    /// The webhook send queue. The real one is a <c>BackgroundService</c> that starts processing on a
    /// thread-pool thread as soon as it is handed an event, outside any request.
    /// </summary>
    public ICallbackBackgroundService Callbacks { get; } = Substitute.For<ICallbackBackgroundService>();

    public ITaskService VsphereTasks { get; } = Substitute.For<ITaskService>();
    public IProxmoxTaskService ProxmoxTasks { get; } = Substitute.For<IProxmoxTaskService>();

    /// <summary>
    /// The one ISO provider this host has, in place of the real pair: <c>IsoService</c> takes providers as
    /// a set, and a test says "one hypervisor stores ISOs, and here is what it holds". Disabled until a
    /// test calls <see cref="EnableIsoProvider"/>, which is what an install with no ISO storage looks like.
    /// </summary>
    public IIsoProvider IsoProvider { get; } = Substitute.For<IIsoProvider>();

    /// <summary>Answers every request the application makes through <c>IHttpClientFactory</c>.</summary>
    public StubHttpMessageHandler OutboundHttp { get; } = new();

    /// <summary>The database the host itself migrated at startup, for the harness's own tests.</summary>
    internal string HostDatabaseName => _hostSession.DatabaseName;

    /// <summary>The usage-log database the host itself migrated at startup, for the harness's own tests.</summary>
    internal string HostLoggingDatabaseName => _hostSession.LoggingDatabaseName();

    /// <remarks>
    /// xUnit initializes a class fixture before constructing the test class, and
    /// <c>WebApplicationFactory</c> builds the host on first use, so the databases exist by the time
    /// <see cref="ConfigureWebHost"/> reads their connection strings.
    /// </remarks>
    public async ValueTask InitializeAsync()
    {
        _hostSession = await database.BeginSessionAsync();
        Player = new PlayerDirectory(PlayerApiClient, Views, RequestUser);
    }

    /// <summary>
    /// The user of the request in flight, as player.api would read it from the token vm.api forwards:
    /// the <c>sub</c> claim, or no one for a request without credentials.
    /// </summary>
    private static Guid? RequestUser() =>
        Guid.TryParse(new HttpContextAccessor().HttpContext?.User.FindFirst("sub")?.Value, out var id) ? id : null;

    /// <summary>
    /// A clean player.api for the next test: the client and team-lookup substitutes cleared and answered
    /// afresh by an empty <see cref="Player"/>, and the host's <c>IMemoryCache</c> emptied, because
    /// <c>PlayerService</c> keeps each caller's system permissions there for a minute, keyed on the user id.
    /// </summary>
    public void ResetPlayer()
    {
        PlayerApiClient.ClearSubstitute();
        Views.ClearSubstitute();
        Player.Reset();

        ((MemoryCache)Services.GetRequiredService<IMemoryCache>()).Clear();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production, so the exception middleware answers a 500 as deployed (the exception message in
        // Detail, not the stack trace).
        builder.UseEnvironment("Production");

        // Host settings reach Program.Main as --key=value, so these win over appsettings.json.
        // InitializeDatabase migrates whatever these name, so they are this host's own clones of the
        // templates (migrating them is a no-op), never the templates themselves.
        builder.UseSetting("Database:Provider", "PostgreSQL");
        builder.UseSetting("ConnectionStrings:PostgreSQL", _hostSession.ConnectionString);
        // Startup's relational branch calls .Trim() on this unconditionally, so it cannot be left unset.
        builder.UseSetting("VmUsageLogging:PostgreSql", _hostSession.LoggingConnectionString());
        builder.UseSetting("VmUsageLogging:Enabled", VmUsageLoggingEnabled ? "true" : "false");
        builder.UseSetting("Authorization:AuthorizationScope", AuthorizationScope);

        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(TestConfiguration.Values));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);

            ReplaceContextRegistrations(services);

            services.Replace(ServiceDescriptor.Singleton(Vsphere));
            services.Replace(ServiceDescriptor.Singleton(Proxmox));
            services.Replace(ServiceDescriptor.Singleton(PlayerApiClient));
            services.Replace(ServiceDescriptor.Singleton(Views));
            services.Replace(ServiceDescriptor.Singleton(Callbacks));
            services.Replace(ServiceDescriptor.Singleton(VsphereTasks));
            services.Replace(ServiceDescriptor.Singleton(ProxmoxTasks));

            services.RemoveAll<IIsoProvider>();
            services.AddSingleton(IsoProvider);

            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(OutboundHttp));
        });
    }

    /// <summary>Set once the host has started, after <c>Program.Main</c>'s <c>InitializeDatabase</c>.</summary>
    private volatile bool _started;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        _started = true;

        return host;
    }

    /// <summary>
    /// Points <see cref="VmContext"/> and <see cref="VmLoggingContext"/> at the databases of the test that
    /// sent the request, through the shared <see cref="TestDatabaseScope"/>.
    /// </summary>
    /// <remarks>
    /// <c>InitializeDatabase</c> resolves both contexts at startup, outside any request, so until the host
    /// has started such a resolution gets the host's own databases; afterwards it throws, which
    /// <c>HttpHarnessTests</c> asserts. A hub invocation over a live connection is outside a request too,
    /// so a hub method that touched the database would throw; <c>HubConnectionTests</c> drives only
    /// <c>ProgressHub</c>, which touches none. The request scope is the context's <c>ServiceProvider</c>, as
    /// in production, so entity events reach the real handlers.
    /// </remarks>
    private void ReplaceContextRegistrations(IServiceCollection services)
    {
        TestDatabaseScope.ReplaceRegistration<VmContext>(
            services, () => _started ? null : _hostSession);
        TestDatabaseScope.ReplaceRegistration<VmLoggingContext>(
            services, () => _started ? null : (ITestDatabaseSession<VmLoggingContext>)_hostSession);
    }

    /// <summary>
    /// Turns <see cref="IsoProvider"/> into a plausible hypervisor: enabled, able to take the request body
    /// straight through, holding nothing, and passing filenames and writes through successfully.
    /// </summary>
    /// <remarks>
    /// All of it has to be set, not just <c>Enabled</c>: a substitute answers null for
    /// <c>NormalizeFilename</c> and for every <c>Task</c>-returning member, and <c>IsoService</c> reaches
    /// those before it reaches anything a test is asserting. Tests re-stub the one call they are about.
    /// </remarks>
    public void EnableIsoProvider(VmType providerType = VmType.Vsphere)
    {
        IsoProvider.ProviderType.Returns(providerType);
        IsoProvider.Enabled.Returns(true);
        IsoProvider.RequiresStagedFile.Returns(false);
        IsoProvider.NormalizeFilename(Arg.Any<string>()).Returns(x => x.Arg<string>());

        IsoProvider.UploadAsync(Arg.Any<IsoUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new IsoOperationOutcome { FailedHostCount = 0, TotalHostCount = 1 });
        IsoProvider.DeleteAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IsoOperationOutcome { FailedHostCount = 0, TotalHostCount = 1 });

        IsoProvider.ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<IsoListingEntry>>());
    }

    public override async ValueTask DisposeAsync()
    {
        // The host first: it holds pooled connections to the databases the session is about to drop.
        await base.DisposeAsync();

        if (_hostSession is not null)
        {
            await _hostSession.DisposeAsync();
        }
    }
}

/// <summary>
/// A host identical to <see cref="VmAppFactory"/> except that <c>VmUsageLoggingSessionController</c> is
/// live: <c>VmUsageLogging:Enabled</c> is true.
/// </summary>
/// <remarks>
/// A whole second host for one boolean, because a running host cannot be asked to change it:
/// <c>Startup.ConfigureServices</c> reads it once to choose which <c>IVmUsageLoggingService</c> to
/// register, and <c>VmUsageLoggingSessionController</c> reads <c>IOptionsMonitor.CurrentValue</c> in its
/// constructor rather than per request.
/// </remarks>
public sealed class VmUsageLoggingEnabledFactory(DatabaseFixture database) : VmAppFactory(database)
{
    protected override bool VmUsageLoggingEnabled => true;
}
