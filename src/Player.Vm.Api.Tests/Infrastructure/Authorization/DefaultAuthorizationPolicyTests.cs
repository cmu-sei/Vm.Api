// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Player.Vm.Api.Tests.Support;
using AppSystemPermission = Player.Vm.Api.Infrastructure.Authorization.AppSystemPermission;

namespace Player.Vm.Api.Tests.Infrastructure.Authorization;

/// <summary>
/// The default authorization policy <c>Startup.ConfigureServices</c> builds: an authenticated user whose
/// token carries every scope in <c>Authorization:AuthorizationScope</c> (<c>player player-vm</c>), one
/// <c>RequireClaim("scope", ...)</c> per scope. It guards every <c>[Authorize]</c> controller and the three
/// endpoints <c>Startup.Configure</c> maps with <c>RequireAuthorization()</c>: both hubs and the Prometheus
/// scrape at <c>/metrics</c>. Each test identity carries both scopes unless a request names its own
/// (<see cref="TestAuthHandler.ScopeHeader"/>), so these name one scope at a time.
/// </summary>
public class DefaultAuthorizationPolicyTests(DatabaseFixture fixture, VmAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmAppFactory>
{
    private const string Metrics = "/metrics";

    /// <summary>
    /// One route per <c>[Authorize]</c> controller and both hubs' negotiate, asked by an actor holding the
    /// system permission each route asks for (<c>ViewVms</c>, <c>ViewNetworks</c>, <c>ViewViews</c>) whose
    /// token lacks one of the two scopes.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/vms", "player")]
    [InlineData("GET", "/api/vms", "player-vm")]
    [InlineData("GET", "/api/vms/vsphere/{id}", "player")]
    [InlineData("GET", "/api/vms/vsphere/{id}", "player-vm")]
    [InlineData("GET", "/api/vms/proxmox/{id}", "player")]
    [InlineData("GET", "/api/vms/proxmox/{id}", "player-vm")]
    [InlineData("GET", "/api/views/{id}/networks", "player")]
    [InlineData("GET", "/api/views/{id}/networks", "player-vm")]
    [InlineData("GET", "/api/isos", "player")]
    [InlineData("GET", "/api/isos", "player-vm")]
    [InlineData("POST", "/hubs/vm/negotiate?negotiateVersion=1", "player")]
    [InlineData("POST", "/hubs/vm/negotiate?negotiateVersion=1", "player-vm")]
    [InlineData("POST", "/hubs/progress/negotiate?negotiateVersion=1", "player")]
    [InlineData("POST", "/hubs/progress/negotiate?negotiateVersion=1", "player-vm")]
    public async Task EveryRoute_is_forbidden_for_a_caller_whose_token_lacks_one_of_the_default_scopes(
        string method, string route, string scope)
    {
        var response = await SendAsPermittedCaller(method, route, scope);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The control: the same caller and routes with both scopes are not refused by the policy (an unknown
    /// Vm id is a 404 after it).
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/vms", HttpStatusCode.OK)]
    [InlineData("GET", "/api/vms/vsphere/{id}", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/vms/proxmox/{id}", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/views/{id}/networks", HttpStatusCode.OK)]
    [InlineData("GET", "/api/isos", HttpStatusCode.OK)]
    [InlineData("POST", "/hubs/vm/negotiate?negotiateVersion=1", HttpStatusCode.OK)]
    [InlineData("POST", "/hubs/progress/negotiate?negotiateVersion=1", HttpStatusCode.OK)]
    public async Task EveryRoute_answers_a_caller_whose_token_carries_both_default_scopes(
        string method, string route, HttpStatusCode expected)
    {
        var response = await SendAsPermittedCaller(method, route, VmAppFactory.AuthorizationScope);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_answers_a_caller_whose_token_carries_both_default_scopes_with_the_prometheus_text()
    {
        var actor = await Actor().SeedAsync();

        var response = await Client(actor).GetAsync(Metrics, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("# TYPE ", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Metrics_is_unauthorized_without_an_identity()
    {
        var response = await Client().GetAsync(Metrics, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Every system permission there is, so what the caller lacks is the scope alone.
    [Fact]
    public async Task Metrics_is_forbidden_for_a_caller_holding_every_permission_whose_token_lacks_player_vm()
    {
        var actor = await Actor().Holding(Enum.GetValues<AppSystemPermission>()).SeedAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, Metrics);
        request.Headers.Add(TestAuthHandler.ScopeHeader, "player");

        var response = await Client(actor).SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// <paramref name="route"/> sent by an actor holding the system permissions the routes ask for, its token
    /// carrying <paramref name="scopes"/>. <c>{id}</c> is an id nothing has.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsPermittedCaller(string method, string route, string scopes)
    {
        var actor = await Actor()
            .Holding(AppSystemPermission.ViewVms, AppSystemPermission.ViewNetworks, AppSystemPermission.ViewViews)
            .SeedAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), route.Replace("{id}", Guid.NewGuid().ToString()));
        request.Headers.Add(TestAuthHandler.ScopeHeader, scopes);

        return await Client(actor).SendAsync(request, Ct);
    }
}
