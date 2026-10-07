// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api: permissions are the substituted player.api's answers, so "an actor with no permissions" is a
// caller the cleared Factory.PlayerApi answers false for. The host also has databases of its own (for
// InitializeDatabase), and three tests prove nothing reaches them after startup.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute.ClearExtensions;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Vms;
using VmModel = Player.Vm.Api.Features.Vms.Vm;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// Tests for the HTTP harness itself: a harness that routes to the wrong database or authorizes everything
/// reads as a green suite.
/// </summary>
public class HttpHarnessTests(DatabaseFixture fixture, VmAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmAppFactory>
{
    private const string SharedName = "Http Isolation Probe";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        Factory.PlayerApi.ClearSubstitute();
        Factory.Vsphere.ClearSubstitute();
    }

    [Fact]
    public async Task The_swagger_document_is_served()
    {
        await AssertStatus(HttpStatusCode.OK, await Client().GetAsync("/swagger/v1/swagger.json", Ct));
    }

    /// <summary>A request with no identity is answered with a 401; the anonymous client is the case under test.</summary>
    [Fact]
    public async Task A_request_with_no_identity_is_unauthorized()
    {
        await AssertStatus(HttpStatusCode.Unauthorized, await Client().GetAsync("api/vms", Ct));
    }

    /// <summary>
    /// The substituted player.api answered no for this caller, and the endpoint refused it, rather than the
    /// pipeline granting by default.
    /// </summary>
    [Fact]
    public async Task An_actor_with_no_permissions_is_forbidden()
    {
        var actor = await Actor().SeedAsync();

        await AssertStatus(HttpStatusCode.Forbidden, await Client(actor).GetAsync("api/vms", Ct));
    }

    /// <summary>Root's arranged permissions are what the endpoint sees, and the request reads this test's database.</summary>
    [Fact]
    public async Task Root_reads_what_the_test_seeded()
    {
        Factory.AllowEverything();
        var vm = TestData.VsphereVm();
        await Seed(vm);

        var vms = await ReadAsync<List<VmModel>>(await RootClient.GetAsync("api/vms", Ct));

        Assert.Contains(vm.Id, vms.Select(x => x.Id));
    }

    [Fact]
    public Task Two_tests_on_one_host_each_see_only_their_own_rows_first() => CreateSharedNameVm();

    [Fact]
    public Task Two_tests_on_one_host_each_see_only_their_own_rows_second() => CreateSharedNameVm();

    /// <summary>
    /// A request writes to the database of the test that sent it, not to the throwaway database the host
    /// migrated at startup. That fallback exists for <c>InitializeDatabase</c> alone.
    /// </summary>
    [Fact]
    public async Task A_request_writes_to_this_tests_database()
    {
        Assert.NotEqual(Factory.HostDatabaseName, Session.DatabaseName);
        Assert.NotEqual(Factory.HostLoggingDatabaseName, Session.LoggingDatabaseName());
        Factory.AllowEverything();
        var vm = TestData.VsphereVm();
        await Seed(vm);

        // An unstubbed BulkPowerOperation hands the handler a null dictionary, which becomes a 500 after
        // the write. Stubbed so a failure here means the database, not the hypervisor seam.
        Factory.Vsphere.BulkPowerOperation(Arg.Any<Guid[]>(), PowerOperation.PowerOn)
            .Returns(new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        var response = await RootClient.PostAsJsonAsync("api/vms/actions/power-on", new { Ids = new[] { vm.Id } }, Ct);

        await AssertStatus(HttpStatusCode.Accepted, response);

        // The handler sets this before calling the hypervisor, and only this test's database holds the row.
        await using var context = NewContext();
        Assert.True((await context.Vms.AsNoTracking().SingleAsync(x => x.Id == vm.Id, Ct)).HasPendingTasks);
    }

    /// <summary>
    /// A request with no session header fails loudly, rather than falling through to the host's database:
    /// the routing lookup throws and the exception middleware answers 500.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_session_header_is_refused()
    {
        using var unrouted = Factory.CreateClient();
        unrouted.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Root.Id.ToString());

        var response = await unrouted.PostAsJsonAsync(
            "api/vms/actions/power-on", new { Ids = new[] { Guid.NewGuid() } }, Ct);

        await AssertStatus(HttpStatusCode.InternalServerError, response);
    }

    /// <summary>
    /// Once the host has started, a context resolved outside a request throws rather than reaching the
    /// host's own databases: the fallback is for <c>InitializeDatabase</c> only.
    /// </summary>
    [Fact]
    public void A_context_resolved_outside_a_request_after_startup_throws()
    {
        using var scope = Factory.Services.CreateScope();

        Assert.Contains(
            "outside a request",
            Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<VmContext>()).Message);
        Assert.Contains(
            "outside a request",
            Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<VmLoggingContext>()).Message);
    }

    private async Task CreateSharedNameVm()
    {
        Factory.AllowEverything();

        var response = await RootClient.PostAsJsonAsync(
            "api/vms", new VmCreateForm { Id = Guid.NewGuid(), Name = SharedName, TeamIds = [Guid.NewGuid()] }, Ct);

        await AssertStatus(HttpStatusCode.Created, response);

        await using var context = NewContext();
        Assert.Equal(1, await context.Vms.CountAsync(x => x.Name == SharedName, Ct));
    }
}
