// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Player.Api.Client;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Infrastructure.Authorization;
using Xunit;
// The Vm class and the Player.Vm namespace share a name, which is why the API's own code writes
// Domain.Models.Vm everywhere. An alias is clearer than the qualification.
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real Startup in-process. Everything between the HTTP request and the hypervisor client
/// is the production wiring: routing, model binding, the authorization policy, the MediatR pipeline
/// behaviors, the handlers, AutoMapper and EF Core against real PostgreSQL. Only the edges are
/// replaced.
///
/// Substituted, and why:
///   IVsphereService / IProxmoxService - the hypervisors. This is the seam the per-Vm error contract
///     is asserted across: unit tests prove the services build the right dictionary, these prove the
///     dictionary survives the handler, the serializer and the wire.
///   IPlayerService - player.api, reached over HTTP for every authorization decision. Note this is
///     the *only* authorization substitute: VmService.CanAccessVm and the handler's own permission
///     gates still run for real.
///   IViewService / IPlayerApiClient - player.api again, by a different route. IViewService is not
///     optional for any test that writes a Vm: the entity-event handlers that push SignalR
///     notifications resolve a team's View through it inside the same request, and the real
///     implementation builds its own client against ClientSettings:urls:playerApi. IPlayerApiClient is
///     what GetVmPermissions reads a caller's team claims from.
///   ICallbackBackgroundService - the webhook send queue, which processes on a thread pool thread
///     outside any request and so would reach the wrong database.
///   IIsoProvider - ISO storage on a hypervisor. Note IsoService is NOT substituted: the permission
///     gates, scope resolution, filename sanitizing and cross-provider merge are the parts worth
///     running, and only the storage itself is replaced.
///   ITaskService / IProxmoxTaskService - the task pollers, which the CheckTasks pipeline behaviors
///     poke after every power command. Substituted so a test can assert the poke happened without
///     starting a poller.
///
/// Removed: every IHostedService. The background pollers would otherwise start dialing a vCenter
/// that is not there, on their own schedule, in the middle of unrelated tests.
///
/// Replaced: the VmContext and VmLoggingContext registrations, so each request reaches the databases of
/// the test that made it. See <see cref="TestDatabaseScope"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a class fixture, not an assembly fixture, and deliberately so. Tests both arrange return
/// values on and assert <c>Received()</c> against the NSubstitute doubles above; NSubstitute keeps its
/// assertion state per thread, so one set of substitutes shared by every test class would lose and
/// cross-attribute calls once classes ran in parallel. The cost is about a second of host startup per
/// endpoint test class. When that starts to hurt, the fix is hand-written session-keyed fakes rather
/// than one shared host.
/// </para>
/// <para>
/// The host gets throwaway databases of its own, separate from any test's.
/// <c>Program.Main</c> matches neither convention <c>HostFactoryResolver</c> looks for, so
/// <c>WebApplicationFactory</c> falls back to invoking it on a background thread - and <c>Main</c>
/// calls <c>InitializeDatabase</c>, which resolves a <c>VmContext</c> outside any request and calls
/// <c>Migrate</c> on it. Nothing gates that off, so the host has to have somewhere real to migrate.
/// A clone of the already-migrated template makes it a no-op, and pointing it at the template itself
/// would hold a connection there and break every later clone. The same goes for the usage log, which
/// <c>InitializeDatabase</c> migrates too when <see cref="VmUsageLoggingEnabled"/>.
/// </para>
/// </remarks>
public class VmApiFactory(DatabaseFixture database) : WebApplicationFactory<Startup>, IAsyncLifetime
{
    /// <summary>
    /// Also written into configuration, so the principal the test handler mints and the policy
    /// Startup builds out of Authorization:AuthorizationScope cannot drift apart.
    /// </summary>
    private static readonly string[] Scopes = ["player", "player-vm"];

    /// <summary>
    /// The scope behind the privileged policy, written into configuration for the same reason
    /// <see cref="Scopes"/> is. Only <see cref="CreatePrivilegedClient"/> presents it.
    /// </summary>
    private const string PrivilegedScope = "player-vm-privileged";

    /// <summary>The ISO size ceiling this host runs with, in bytes. See where it is written below.</summary>
    public const long MaxIsoFileSize = 4096;

    /// <summary>
    /// The Proxmox cluster this host is configured against. Exposed because it is not only an address:
    /// it is the provider instance id every Proxmox <c>ViewNetwork</c> row is keyed on, so a test seeding
    /// one has to agree with it. Deliberately not the empty string <c>appsettings.json</c> ships, which
    /// is also <c>ViewNetwork.ProviderInstanceId</c>'s default and would let a row belonging to no
    /// cluster in particular match.
    /// </summary>
    public const string ProxmoxHost = "pve.test";

    private TestDatabaseSession _hostSession;

    /// <summary>
    /// Whether this host runs with <c>VmUsageLogging:Enabled</c>. False, which is what
    /// <c>appsettings.json</c> ships and what every deployment that does not want a usage log runs with -
    /// so it is what the rest of the endpoint tests should be proving things against.
    /// </summary>
    /// <remarks>
    /// It is a host setting rather than something a test can flip, and so needs a second host to cover
    /// both sides: <c>Startup</c> reads it once to choose between <c>VmUsageLoggingService</c> and
    /// <c>DisabledVmUsageLoggingService</c>, and <c>VmUsageLoggingSessionController</c> captures
    /// <c>IOptionsMonitor.CurrentValue</c> in its constructor. See
    /// <see cref="VmUsageLoggingEnabledFactory"/>.
    /// </remarks>
    protected virtual bool VmUsageLoggingEnabled => false;

    public IVsphereService Vsphere { get; } = Substitute.For<IVsphereService>();
    public IProxmoxService Proxmox { get; } = Substitute.For<IProxmoxService>();
    public IPlayerService PlayerApi { get; } = Substitute.For<IPlayerService>();

    /// <summary>
    /// The generated player.api client, as <c>GetVmPermissions</c> consumes it. Distinct from
    /// <see cref="PlayerApi"/>, which is this application's own wrapper over it.
    /// </summary>
    public IPlayerApiClient PlayerApiClient { get; } = Substitute.For<IPlayerApiClient>();

    /// <summary>
    /// Resolves a team to the View it belongs to. Reached by the SignalR entity-event handlers on every
    /// Vm write, so a test that creates, updates or deletes a Vm goes through it whether it means to or
    /// not; unstubbed it answers null, which those handlers treat as "no View group to notify".
    /// </summary>
    public IViewService Views { get; } = Substitute.For<IViewService>();

    /// <summary>
    /// The webhook send queue. Substituted rather than left real because the real one is a
    /// <c>BackgroundService</c> that builds its <c>ActionBlock</c> in its constructor: handing it an event
    /// starts processing on a thread pool thread, outside any request, so it resolves the host's own
    /// <c>VmContext</c> and races the test that is asserting on the row it just wrote.
    /// </summary>
    public ICallbackBackgroundService Callbacks { get; } = Substitute.For<ICallbackBackgroundService>();

    public ITaskService VsphereTasks { get; } = Substitute.For<ITaskService>();
    public IProxmoxTaskService ProxmoxTasks { get; } = Substitute.For<IProxmoxTaskService>();

    /// <summary>
    /// The one ISO provider this host has. The real pair is removed rather than substituted one for one,
    /// because <c>IsoService</c> takes providers as a set: what a test needs to say is "one hypervisor
    /// stores ISOs, and here is what it holds", not which two happen to be registered. <c>IsoService</c>
    /// itself is left real, so the scope resolution, the permission gates, the filename sanitizing and
    /// the cross-provider merge all run.
    /// </summary>
    /// <remarks>
    /// Disabled until a test calls <see cref="EnableIsoProvider"/>, which is what an install with no ISO
    /// storage configured looks like - and is what every endpoint test that has nothing to do with ISOs
    /// should see.
    /// </remarks>
    public IIsoProvider IsoProvider { get; } = Substitute.For<IIsoProvider>();

    public Guid UserId { get; } = Guid.NewGuid();

    /// <summary>
    /// The database the host itself migrated at startup. Exposed so the harness's own tests can prove
    /// no request ever reads or writes it.
    /// </summary>
    internal string HostDatabaseName => _hostSession.DatabaseName;

    /// <summary>
    /// The usage log database the host itself migrated at startup, exposed for the same reason
    /// <see cref="HostDatabaseName"/> is.
    /// </summary>
    internal string HostLoggingDatabaseName => _hostSession.LoggingDatabaseName;

    /// <remarks>
    /// xUnit initializes a class fixture before constructing the test class, and
    /// <c>WebApplicationFactory</c> does not build the host until it is first used, so the database
    /// exists by the time <see cref="ConfigureWebHost"/> reads its connection string.
    /// </remarks>
    public async ValueTask InitializeAsync() => _hostSession = await database.BeginSessionAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("Database:Provider", "PostgreSQL");
        builder.UseSetting("ConnectionStrings:PostgreSQL", _hostSession.ConnectionString);
        // Startup's relational branch calls .Trim() on this unconditionally, so it cannot be left unset.
        // The host's own logging database, for the same reason it has its own VmContext database: with
        // logging enabled, InitializeDatabase migrates this one too.
        builder.UseSetting("VmUsageLogging:PostgreSql", _hostSession.LoggingConnectionString);
        builder.UseSetting("VmUsageLogging:Enabled", VmUsageLoggingEnabled ? "true" : "false");
        // Its UI needs a stylesheet from the content root and adds a poller of its own; neither has
        // anything to do with the API surface under test.
        builder.UseSetting("HealthChecksUI:Enabled", "false");
        builder.UseSetting("Authorization:AuthorizationScope", string.Join(' ', Scopes));
        builder.UseSetting("Authorization:PrivilegedScope", PrivilegedScope);
        // Small enough that a test can exceed it without moving gigabytes. This one value is both the
        // limit UploadIso enforces and the multipart body limit Startup hands Kestrel, so it is the only
        // way to reach either check over a real request.
        builder.UseSetting("IsoUpload:MaxFileSize", MaxIsoFileSize.ToString());
        // Nothing dials it - IProxmoxService is substituted, so no PveClient is ever built - but the
        // Proxmox request path reads it as the provider instance id its view-network rows are keyed on.
        builder.UseSetting("Proxmox:Host", ProxmoxHost);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.Replace(ServiceDescriptor.Singleton(Vsphere));
            services.Replace(ServiceDescriptor.Singleton(Proxmox));
            services.Replace(ServiceDescriptor.Singleton(PlayerApi));
            services.Replace(ServiceDescriptor.Singleton(PlayerApiClient));
            services.Replace(ServiceDescriptor.Singleton(Views));
            services.Replace(ServiceDescriptor.Singleton(Callbacks));
            services.Replace(ServiceDescriptor.Singleton(VsphereTasks));
            services.Replace(ServiceDescriptor.Singleton(ProxmoxTasks));

            services.RemoveAll<IIsoProvider>();
            services.AddSingleton(IsoProvider);

            AddPerTestDatabase(services);

            // Last AddAuthentication wins on the default scheme, so this displaces JWT bearer
            // without having to unpick its registration.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<TestAuthOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, o =>
                    {
                        o.Scopes = Scopes;
                        o.PrivilegedScope = PrivilegedScope;
                    });
        });
    }

    /// <summary>
    /// Points <see cref="VmContext"/> and <see cref="VmLoggingContext"/> at the databases of the test
    /// making the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request scope is passed as the context's <c>ServiceProvider</c>, which is what the
    /// application's own registration does, so <c>PublishEventsAsync</c> resolves the request's real
    /// mediator and entity events reach the real handlers.
    /// </para>
    /// <para>
    /// The pooled factory registration goes too. Nothing in the application resolves it, and leaving it
    /// would let a stray resolution reach a context bound to whichever connection string was baked in
    /// when the container was built.
    /// </para>
    /// <para>
    /// Which database a resolution reaches is <see cref="SessionFor"/>'s decision.
    /// </para>
    /// </remarks>
    private void AddPerTestDatabase(IServiceCollection services)
    {
        services.RemoveAll<VmContext>();
        services.RemoveAll<IDbContextFactory<VmContext>>();

        services.AddScoped(provider => SessionFor(provider).CreateContext(provider));

        // The usage log the same way, and for the same reason: AddDbContextPool bakes one connection
        // string into one pooled set of options when the container is built. The pool registrations
        // behind it are left in place - nothing resolves them once the context itself is replaced.
        services.RemoveAll<VmLoggingContext>();

        services.AddScoped(provider => SessionFor(provider).CreateLoggingContext());
    }

    /// <summary>
    /// The database session a resolution belongs to: the test that made the request, or the host's own
    /// when there is no request.
    /// </summary>
    /// <remarks>
    /// Falling back rather than throwing because <c>InitializeDatabase</c> does exactly this during
    /// startup. It is the one place a context is legitimately resolved outside a request; a test wanting
    /// one should take it from <c>DatabaseTestBase.Db</c> or <c>NewContext()</c>, and
    /// <c>DatabaseHarnessTests</c> asserts that requests never land here.
    /// </remarks>
    private TestDatabaseSession SessionFor(IServiceProvider provider)
    {
        var httpContext = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;

        return httpContext is null
            ? _hostSession
            : TestDatabaseScope.Resolve(httpContext);
    }

    /// <summary>
    /// A client whose requests authenticate as <see cref="UserId"/>. Use CreateClient() directly for
    /// the anonymous case.
    /// </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, UserId.ToString());
        return client;
    }

    /// <summary>
    /// A client whose requests also carry the privileged scope, as the machine-to-machine callers of
    /// <c>CallbacksController</c> do. Everything a <see cref="CreateAuthenticatedClient"/> request can
    /// reach, this one can reach too - the privileged scope is added to the ordinary ones, not swapped
    /// for them, which is what a real privileged token looks like.
    /// </summary>
    public HttpClient CreatePrivilegedClient()
    {
        var client = CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.PrivilegedHeader, "true");
        return client;
    }

    /// <summary>
    /// Grants the substituted player.api every team-scoped and system-wide permission the endpoints
    /// gate on. Tests that care about a denial re-stub just the call they are denying.
    /// </summary>
    /// <remarks>
    /// Deliberately not exhaustive over <c>IPlayerService</c>: the visibility calls
    /// (<c>GetVisibilityContextAsync</c> and friends) are left unstubbed, because a default that made
    /// every team visible would hide the difference between a permission and a team's membership of a
    /// View. Tests wire those per case.
    /// </remarks>
    public void AllowEverything()
    {
        PlayerApi.CanViewTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        PlayerApi.CanEditTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        PlayerApi.CanManageTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        PlayerApi.Can(
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<AppSystemPermission[]>(),
                Arg.Any<AppViewPermission[]>(),
                Arg.Any<AppTeamPermission[]>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
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

    /// <summary>
    /// A Vsphere Vm that passes every gate in BulkPowerOperation: a supported type, a power state the
    /// vSphere path accepts (Unknown is rejected outright), and a team to authorize against.
    /// </summary>
    /// <remarks>
    /// The team id is not a foreign key - teams live in player.api, and <c>VmTeam</c>'s only
    /// relationship is to <c>Vm</c> - so an arbitrary Guid is valid seed data even now that
    /// constraints are enforced for real.
    /// </remarks>
    public static VmEntity VsphereVm(
        Guid? teamId = null,
        PowerState powerState = PowerState.Off,
        bool hasSnapshot = false)
    {
        var id = Guid.NewGuid();

        return new VmEntity
        {
            Id = id,
            Name = $"vm-{id}",
            Type = VmType.Vsphere,
            PowerState = powerState,
            HasSnapshot = hasSnapshot,
            VmTeams = [new VmTeam(teamId ?? Guid.NewGuid(), id)]
        };
    }

    /// <summary>
    /// A Proxmox Vm that passes every gate in BulkPowerOperation, as <see cref="VsphereVm"/> is for the
    /// other hypervisor. Note what it does not need: a power state the handler accepts, because the
    /// Proxmox path deliberately does not gate on one.
    /// </summary>
    /// <remarks>
    /// The <c>ProxmoxVmInfo</c> row is what makes this a Proxmox Vm rather than a row that merely says
    /// so. Nothing in the bulk handler reads it - it hands ids to <see cref="Proxmox"/>, which is
    /// substituted - but the real <c>ProxmoxService.BulkPowerOperation</c> looks the Vm up by it and
    /// reports "Virtual machine not found" for an id it cannot find, so a seed without one would be a Vm
    /// the production service could not have acted on.
    /// </remarks>
    public static VmEntity ProxmoxVm(
        Guid? teamId = null,
        PowerState powerState = PowerState.Off,
        int vmid = 100)
    {
        var id = Guid.NewGuid();

        return new VmEntity
        {
            Id = id,
            Name = $"vm-{id}",
            Type = VmType.Proxmox,
            PowerState = powerState,
            VmTeams = [new VmTeam(teamId ?? Guid.NewGuid(), id)],
            ProxmoxVmInfo = new ProxmoxVmInfo { Id = vmid, Node = "pve-1" },
        };
    }

    public override async ValueTask DisposeAsync()
    {
        // The host first: it holds pooled connections to the database the session is about to drop.
        await base.DisposeAsync();

        if (_hostSession is not null)
        {
            await _hostSession.DisposeAsync();
        }
    }
}
