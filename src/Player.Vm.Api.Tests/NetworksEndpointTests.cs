// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using ViewNetworkEntity = Player.Vm.Api.Domain.Models.ViewNetwork;
using ViewNetworkModel = Player.Vm.Api.Features.Networks.ViewNetwork;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The view-network endpoints in process, through the real Startup and against real PostgreSQL.
///
/// What these add over <see cref="NetworkServiceTests"/>: that the routes are where the generated client
/// expects them, that the service's exceptions become the status codes a caller can act on rather than
/// 500s, that Create reports a Location a caller can follow, and that none of it is reachable without
/// credentials. Those live in the middleware, the attributes and the route table - none of which a
/// service-level test touches.
/// </summary>
public class NetworksEndpointTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    private const string Instance = "vcenter-1";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        Factory.PlayerApi.ClearSubstitute();
        Factory.AllowEverything();
    }

    #region Reads

    [Fact]
    public async Task GetByViewId_ReturnsTheViewsNetworks()
    {
        var viewId = Guid.NewGuid();
        var mine = Network(viewId, "vlan-10");
        var other = Network(Guid.NewGuid(), "vlan-20");
        await Seed(mine, other);

        var networks = await Get<ViewNetworkModel[]>($"/api/views/{viewId}/networks");

        Assert.Equal<Guid>([mine.Id], networks.Select(x => x.Id).ToArray());
    }

    // The team ids are what the UI needs to show which teams a network is offered to, so they have to
    // survive the mapping profile and the serializer rather than arriving as an empty array.
    [Fact]
    public async Task GetById_ReturnsTheNetworkWithItsTeamIds()
    {
        var teamId = Guid.NewGuid();
        var network = Network(Guid.NewGuid(), "vlan-10", teamIds: [teamId], name: "user network");
        await Seed(network);

        var body = await Get<ViewNetworkModel>($"/api/views/{network.ViewId}/networks/{network.Id}");

        Assert.Equal(network.Id, body.Id);
        Assert.Equal("user network", body.Name);
        Assert.Equal<Guid>([teamId], body.TeamIds);
    }

    /// <summary>
    /// Enums go out as their name, not their number. Asserted against the raw JSON because every other
    /// test here deserializes with the application's own options and so would follow the format wherever
    /// it went; the generated clients would not, and neither would a stored integration.
    /// </summary>
    [Fact]
    public async Task GetById_SerializesAnEnumAsItsName()
    {
        var network = Network(Guid.NewGuid(), "vlan-10");
        await Seed(network);

        var json = await Client.GetStringAsync(
            $"/api/views/{network.ViewId}/networks/{network.Id}", Ct);

        Assert.Contains("\"providerType\":\"Vsphere\"", json);
    }

    [Fact]
    public async Task GetById_ForANetworkInAnotherView_Is404()
    {
        var network = Network(Guid.NewGuid(), "vlan-10");
        await Seed(network);

        var response = await Client.GetAsync($"/api/views/{Guid.NewGuid()}/networks/{network.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Writes

    /// <summary>
    /// 201 with a Location a caller can follow. The route is built by action name, so renaming GetById
    /// without updating the CreatedAtAction would leave the response without one.
    /// </summary>
    [Fact]
    public async Task Create_Returns201AndALocationThatResolves()
    {
        var viewId = Guid.NewGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/views/{viewId}/networks", Form("vlan-10", "user network"), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<ViewNetworkModel>(JsonOptions, Ct);
        var followed = await Get<ViewNetworkModel>(response.Headers.Location.ToString());

        Assert.Equal(created.Id, followed.Id);
        Assert.Equal("vlan-10", followed.NetworkId);
    }

    /// <summary>
    /// Documented as idempotent: posting the same view, provider, instance and network id again returns
    /// the network that already exists rather than a second row or a conflict. The caller is a
    /// provisioning script that cannot tell whether it has run before.
    /// </summary>
    [Fact]
    public async Task Create_RepeatedForTheSameNetwork_ReturnsTheExistingOne()
    {
        var viewId = Guid.NewGuid();

        var first = await Created($"/api/views/{viewId}/networks", Form("vlan-10", "first"));
        var second = await Created($"/api/views/{viewId}/networks", Form("vlan-10", "second"));

        Assert.Equal(first.Id, second.Id);

        // The existing row wins, so the name from the second post is not applied.
        Assert.Equal("first", second.Name);

        await using var context = NewContext();
        Assert.Single(context.ViewNetworks.Where(x => x.ViewId == viewId));
    }

    // The same network id under a different view is a different network, so this is a create and not a
    // match against the row that already exists.
    [Fact]
    public async Task Create_ForTheSameNetworkIdInAnotherView_CreatesASecondRow()
    {
        var first = await Created($"/api/views/{Guid.NewGuid()}/networks", Form("vlan-10", "first"));
        var second = await Created($"/api/views/{Guid.NewGuid()}/networks", Form("vlan-10", "second"));

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Update_ReplacesTheNetwork()
    {
        var network = Network(Guid.NewGuid(), "vlan-10", name: "before");
        await Seed(network);

        var teamId = Guid.NewGuid();
        var form = Form("vlan-10", "after");
        form.TeamIds = [teamId];

        var response = await Client.PutAsJsonAsync(
            $"/api/views/{network.ViewId}/networks/{network.Id}", form, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ViewNetworkModel>(JsonOptions, Ct);
        Assert.Equal("after", body.Name);
        Assert.Equal<Guid>([teamId], body.TeamIds);
    }

    /// <summary>
    /// A BadRequestException from the service has to arrive as a 400. It is the only status in this
    /// controller that comes from a rule rather than a permission or a missing row, so a caller that
    /// retried a 500 here would retry forever.
    /// </summary>
    [Fact]
    public async Task Update_OntoANetworkThatAlreadyExists_Is400()
    {
        var viewId = Guid.NewGuid();
        var moving = Network(viewId, "vlan-10");
        var occupied = Network(viewId, "vlan-20");
        await Seed(moving, occupied);

        var response = await Client.PutAsJsonAsync(
            $"/api/views/{viewId}/networks/{moving.Id}", Form("vlan-20", "collides"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns204AndRemovesTheRow()
    {
        var network = Network(Guid.NewGuid(), "vlan-10");
        await Seed(network);

        var response = await Client.DeleteAsync(
            $"/api/views/{network.ViewId}/networks/{network.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.ViewNetworks.Where(x => x.Id == network.Id));
    }

    [Fact]
    public async Task Delete_ForANetworkInAnotherView_Is404()
    {
        var network = Network(Guid.NewGuid(), "vlan-10");
        await Seed(network);

        var response = await Client.DeleteAsync($"/api/views/{Guid.NewGuid()}/networks/{network.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.ViewNetworks.Where(x => x.Id == network.Id));
    }

    #endregion

    #region Authorization

    /// <summary>
    /// Every route on the controller is behind the default authorization policy, which the substituted
    /// player.api has no say in. Driven as a theory because the attribute is on the class: a route added
    /// to a controller that had lost its <c>[Authorize]</c> would answer anonymously, and only a check
    /// per route notices.
    /// </summary>
    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/{id}")]
    [InlineData("POST", "")]
    [InlineData("PUT", "/{id}")]
    [InlineData("DELETE", "/{id}")]
    public async Task EveryRoute_RejectsAnUnauthenticatedRequest(string method, string suffix)
    {
        var route = $"/api/views/{Guid.NewGuid()}/networks{suffix.Replace("{id}", Guid.NewGuid().ToString())}";

        using var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = JsonContent.Create(Form("vlan-10", "network"))
        };

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A ForbiddenException from the service is a 403, not a 500 and not an empty 200.
    [Fact]
    public async Task Reads_WithoutANetworkPermission_Are403()
    {
        var network = Network(Guid.NewGuid(), "vlan-10");
        await Seed(network);
        DenyEverything();

        var byView = await Client.GetAsync($"/api/views/{network.ViewId}/networks", Ct);
        var byId = await Client.GetAsync($"/api/views/{network.ViewId}/networks/{network.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, byView.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byId.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutANetworkPermission_Is403AndLeavesTheRow()
    {
        var network = Network(Guid.NewGuid(), "vlan-10");
        await Seed(network);
        DenyEverything();

        var response = await Client.DeleteAsync(
            $"/api/views/{network.ViewId}/networks/{network.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.ViewNetworks.Where(x => x.Id == network.Id));
    }

    /// <summary>
    /// player.api answering 404 for a view is a 404 here, not a 500: the middleware unwraps
    /// <c>Player.Api.Client.ApiException</c> for that one status. A caller asking about a view that has
    /// been deleted is the ordinary case, and it must not read as this API being broken.
    /// </summary>
    [Fact]
    public async Task Reads_WhenPlayerApiReportsTheViewIsGone_Are404()
    {
        Factory.PlayerApi
            .Can(default, default, default, default, default, Ct)
            .ReturnsForAnyArgs<bool>(_ => throw new Player.Api.Client.ApiException(
                "View not found", (int)HttpStatusCode.NotFound, null, null, null));

        var response = await Client.GetAsync($"/api/views/{Guid.NewGuid()}/networks", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Any other failure from player.api is a 500. Reporting it as anything a caller could act on would
    // be a lie, and reporting an outage as 403 would send a user to their administrator instead.
    [Fact]
    public async Task Reads_WhenPlayerApiIsUnreachable_Are500()
    {
        Factory.PlayerApi
            .Can(default, default, default, default, default, Ct)
            .ReturnsForAnyArgs<bool>(_ => throw new Player.Api.Client.ApiException(
                "Service Unavailable", (int)HttpStatusCode.ServiceUnavailable, null, null, null));

        var response = await Client.GetAsync($"/api/views/{Guid.NewGuid()}/networks", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    #endregion

    #region Helpers

    private async Task<T> Get<T>(string route)
    {
        var response = await Client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    private async Task<ViewNetworkModel> Created(string route, CreateViewNetworkForm form)
    {
        var response = await Client.PostAsJsonAsync(route, form, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<ViewNetworkModel>(JsonOptions, Ct);
    }

    private void DenyEverything() =>
        Factory.PlayerApi
            .Can(default, default, default, default, default, Ct)
            .ReturnsForAnyArgs(false);

    private static CreateViewNetworkForm Form(string networkId, string name) =>
        new()
        {
            NetworkId = networkId,
            Name = name,
            ProviderType = VmType.Vsphere,
            ProviderInstanceId = Instance
        };

    private static ViewNetworkEntity Network(
        Guid viewId, string networkId, Guid[] teamIds = null, string name = "network") =>
        new()
        {
            Id = Guid.NewGuid(),
            ViewId = viewId,
            NetworkId = networkId,
            Name = name,
            TeamIds = teamIds ?? [],
            ProviderInstanceId = Instance,
            ProviderType = VmType.Vsphere
        };

    #endregion
}
