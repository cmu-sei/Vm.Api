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
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Vsphere.Services;
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
///   ITaskService / IProxmoxTaskService - the task pollers, which the CheckTasks pipeline behaviors
///     poke after every power command. Substituted so a test can assert the poke happened without
///     starting a poller.
///
/// Removed: every IHostedService. The background pollers would otherwise start dialing a vCenter
/// that is not there, on their own schedule, in the middle of unrelated tests.
///
/// Replaced: the scoped VmContext registration, so each request reaches the database of the test that
/// made it. See <see cref="TestDatabaseScope"/>.
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
/// The host gets a throwaway database of its own, separate from any test's.
/// <c>Program.Main</c> matches neither convention <c>HostFactoryResolver</c> looks for, so
/// <c>WebApplicationFactory</c> falls back to invoking it on a background thread - and <c>Main</c>
/// calls <c>InitializeDatabase</c>, which resolves a <c>VmContext</c> outside any request and calls
/// <c>Migrate</c> on it. Nothing gates that off, so the host has to have somewhere real to migrate.
/// A clone of the already-migrated template makes it a no-op, and pointing it at the template itself
/// would hold a connection there and break every later clone.
/// </para>
/// </remarks>
public class VmApiFactory(DatabaseFixture database) : WebApplicationFactory<Startup>, IAsyncLifetime
{
    /// <summary>
    /// Also written into configuration, so the principal the test handler mints and the policy
    /// Startup builds out of Authorization:AuthorizationScope cannot drift apart.
    /// </summary>
    private static readonly string[] Scopes = ["player", "player-vm"];

    private TestDatabaseSession _hostSession;

    public IVsphereService Vsphere { get; } = Substitute.For<IVsphereService>();
    public IProxmoxService Proxmox { get; } = Substitute.For<IProxmoxService>();
    public IPlayerService PlayerApi { get; } = Substitute.For<IPlayerService>();
    public ITaskService VsphereTasks { get; } = Substitute.For<ITaskService>();
    public IProxmoxTaskService ProxmoxTasks { get; } = Substitute.For<IProxmoxTaskService>();

    public Guid UserId { get; } = Guid.NewGuid();

    /// <summary>
    /// The database the host itself migrated at startup. Exposed so the harness's own tests can prove
    /// no request ever reads or writes it.
    /// </summary>
    internal string HostDatabaseName => _hostSession.DatabaseName;

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
        // Startup's relational branch calls .Trim() on this unconditionally, so it cannot be left
        // unset. Enabled is false, so nothing ever migrates or writes to it.
        builder.UseSetting("VmUsageLogging:PostgreSql", _hostSession.ConnectionString);
        builder.UseSetting("VmUsageLogging:Enabled", "false");
        // Its UI needs a stylesheet from the content root and adds a poller of its own; neither has
        // anything to do with the API surface under test.
        builder.UseSetting("HealthChecksUI:Enabled", "false");
        builder.UseSetting("Authorization:AuthorizationScope", string.Join(' ', Scopes));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.Replace(ServiceDescriptor.Singleton(Vsphere));
            services.Replace(ServiceDescriptor.Singleton(Proxmox));
            services.Replace(ServiceDescriptor.Singleton(PlayerApi));
            services.Replace(ServiceDescriptor.Singleton(VsphereTasks));
            services.Replace(ServiceDescriptor.Singleton(ProxmoxTasks));

            AddPerTestDatabase(services);

            // Last AddAuthentication wins on the default scheme, so this displaces JWT bearer
            // without having to unpick its registration.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<TestAuthOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, o => o.Scopes = Scopes);
        });
    }

    /// <summary>
    /// Points <see cref="VmContext"/> at the database of the test making the request.
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
    /// Resolving with no <c>HttpContext</c> yields the host's own database rather than throwing,
    /// because <c>InitializeDatabase</c> does exactly that during startup. It is the one place a
    /// context is legitimately resolved outside a request; a test wanting one should take it from
    /// <c>DatabaseTestBase.Db</c> or <c>NewContext()</c>, and <c>DatabaseHarnessTests</c> asserts that
    /// requests never land here.
    /// </para>
    /// </remarks>
    private void AddPerTestDatabase(IServiceCollection services)
    {
        services.RemoveAll<VmContext>();
        services.RemoveAll<IDbContextFactory<VmContext>>();

        services.AddScoped(provider =>
        {
            var httpContext = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;

            var session = httpContext is null
                ? _hostSession
                : TestDatabaseScope.Resolve(httpContext);

            return session.CreateContext(provider);
        });
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
    /// Grants the substituted player.api every permission the power-operation gates ask for. Tests
    /// that care about a denial re-stub just the call they are denying.
    /// </summary>
    public void AllowEverything()
    {
        PlayerApi.CanViewTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        PlayerApi.CanEditTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
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
