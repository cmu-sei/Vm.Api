// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Domain.Services.HealthChecks;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The two health routes in process. Their caller is an orchestrator's probe, so what matters is not a
/// model or a permission but which checks each route runs and what a failure looks like on the wire.
///
/// The split is the point: liveliness must not fail because a dependency is down, or the orchestrator
/// restarts a container that is working, and readiness must fail while a dependency is down, or traffic
/// arrives before the API can serve it. That split lives entirely in the "live" and "ready" tags on the
/// registrations in <c>Startup</c>, which nothing else asserts.
/// </summary>
public class HealthEndpointTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    /// <summary>
    /// The vSphere connection check reports on state a background service owns, and this host has none
    /// running. Each test sets the state it needs, so reset it here rather than inheriting the last one's.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        ConnectionCheck.StartupCheckComplete = false;
        ConnectionCheck.Connections = [];
    }

    private ConnectionServiceHealthCheck ConnectionCheck =>
        Factory.Services.GetRequiredService<ConnectionServiceHealthCheck>();

    /// <summary>
    /// A probe carries no token, and a 401 here reads to an orchestrator exactly like a dead process.
    /// </summary>
    /// <remarks>
    /// Note what does the work today: nothing. There is no global authorization filter and
    /// <c>MapControllers</c> adds no requirement, so the controller's <c>[AllowAnonymous]</c> is
    /// decorative - removing it changes nothing, and this test does not notice. What it does notice is a
    /// requirement arriving from any direction: an <c>[Authorize]</c> here without the
    /// <c>[AllowAnonymous]</c> to override it, a fallback policy, or a <c>RequireAuthorization</c> on the
    /// controller endpoints.
    /// </remarks>
    [Theory]
    [InlineData("live")]
    [InlineData("ready")]
    public async Task Health_IsReachableWithoutCredentials(string route)
    {
        var response = await AnonymousClient.GetAsync($"/api/health/{route}", Ct);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Liveliness runs the task service check alone. Adding a dependency to this set - the database, a
    /// hypervisor - would have an orchestrator killing the process whenever that dependency blinked.
    /// </summary>
    [Fact]
    public async Task Live_RunsOnlyTheTaskServiceCheck()
    {
        Assert.Equal<string>(["task_service"], await Checks("live"));
    }

    /// <summary>
    /// Readiness runs the dependencies a request needs and not the task service, which polls on its own
    /// schedule and has nothing to do with whether this instance can answer.
    /// </summary>
    [Fact]
    public async Task Ready_RunsTheDependencyChecksAndNotTheTaskServiceCheck()
    {
        Assert.Equal<string>(["connection_service", "database"], await Checks("ready"));
    }

    /// <summary>
    /// The database check against the real PostgreSQL the host is pointed at. This is the one check whose
    /// answer is not synthesized from in-process state, so it is the one that would notice a connection
    /// string that parses and does not connect.
    /// </summary>
    [Fact]
    public async Task Ready_ReportsTheDatabaseAsHealthy()
    {
        Assert.Equal("Healthy", await Status("ready", "database"));
    }

    // Nothing has told the connection check that the startup attempts finished, which is the state a
    // just-started instance is in, and it must not report ready in it.
    [Fact]
    public async Task Ready_BeforeTheVsphereStartupCheckFinishes_ReportsUnhealthy()
    {
        Assert.Equal("Unhealthy", await Status("ready", "connection_service"));
        Assert.Equal("Unhealthy", await Status("ready"));
    }

    [Fact]
    public async Task Ready_OnceEveryCheckPasses_ReportsHealthy()
    {
        ConnectionCheck.StartupCheckComplete = true;

        Assert.Equal("Healthy", await Status("ready"));
    }

    /// <summary>
    /// Pins observed behavior rather than intended behavior, and is worth having for exactly that reason.
    /// These are controller actions, not the health check middleware, and
    /// <c>UIResponseWriter.WriteHealthCheckUIResponse</c> writes the report without setting a status code -
    /// so an unhealthy readiness check is a 200 whose body says "Unhealthy". A probe configured on the
    /// status code alone never fires. If that is ever fixed, this test is the one that fails, and the fix
    /// is to assert 503 here.
    /// </summary>
    [Fact]
    public async Task Ready_WhenUnhealthy_StillAnswers200()
    {
        var response = await AnonymousClient.GetAsync("/api/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Unhealthy", Parse(await response.Content.ReadAsStringAsync(Ct))
            .GetProperty("status").GetString());
    }

    #region Helpers

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<JsonElement> Report(string route)
    {
        var response = await AnonymousClient.GetAsync($"/api/health/{route}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The names of the checks the route ran, sorted - the report does not order them.</summary>
    private async Task<string[]> Checks(string route) =>
        [.. (await Report(route)).GetProperty("entries")
            .EnumerateObject()
            .Select(x => x.Name)
            .Order()];

    /// <summary>The overall status of the route's report.</summary>
    private async Task<string> Status(string route) =>
        (await Report(route)).GetProperty("status").GetString();

    /// <summary>The status of one named check within the route's report.</summary>
    private async Task<string> Status(string route, string check) =>
        (await Report(route)).GetProperty("entries").GetProperty(check)
            .GetProperty("status").GetString();

    #endregion
}
