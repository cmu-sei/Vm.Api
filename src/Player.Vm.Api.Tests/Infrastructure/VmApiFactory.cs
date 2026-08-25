// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
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
// The Vm class and the Player.Vm namespace share a name, which is why the API's own code writes
// Domain.Models.Vm everywhere. An alias is clearer than the qualification.
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real Startup in-process. Everything between the HTTP request and the hypervisor client
/// is the production wiring: routing, model binding, the authorization policy, the MediatR pipeline
/// behaviors, the handlers, AutoMapper and EF Core. Only the edges are replaced.
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
/// One caveat: Startup hardcodes the in-memory database name ("vm"), so every factory in a test run
/// shares one store. Tests must therefore scope their assertions to Vms they seeded themselves rather
/// than to the whole table.
/// </summary>
public class VmApiFactory : WebApplicationFactory<Startup>
{
    /// <summary>
    /// Also written into configuration, so the principal the test handler mints and the policy
    /// Startup builds out of Authorization:AuthorizationScope cannot drift apart.
    /// </summary>
    private static readonly string[] Scopes = ["player", "player-vm"];

    public IVsphereService Vsphere { get; } = Substitute.For<IVsphereService>();
    public IProxmoxService Proxmox { get; } = Substitute.For<IProxmoxService>();
    public IPlayerService PlayerApi { get; } = Substitute.For<IPlayerService>();
    public ITaskService VsphereTasks { get; } = Substitute.For<ITaskService>();
    public IProxmoxTaskService ProxmoxTasks { get; } = Substitute.For<IProxmoxTaskService>();

    public Guid UserId { get; } = Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("Database:Provider", "InMemory");
        builder.UseSetting("Database:AutoMigrate", "false");
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

            // Last AddAuthentication wins on the default scheme, so this displaces JWT bearer
            // without having to unpick its registration.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<TestAuthOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, o => o.Scopes = Scopes);
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

    public async Task SeedAsync(params VmEntity[] vms)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VmContext>();

        context.Vms.AddRange(vms);
        await context.SaveChangesAsync();
    }

    /// <summary>Reads a Vm back from the store, outside any request scope.</summary>
    public async Task<VmEntity> ReadAsync(Guid id)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VmContext>();

        return await context.Vms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    }

    /// <summary>
    /// A Vsphere Vm that passes every gate in BulkPowerOperation: a supported type, a power state the
    /// vSphere path accepts (Unknown is rejected outright), and a team to authorize against.
    /// </summary>
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
}
