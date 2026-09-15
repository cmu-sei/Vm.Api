// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Player.Api.Client;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using AppTeamPermission = Player.Vm.Api.Infrastructure.Authorization.AppTeamPermission;
using AppViewPermission = Player.Vm.Api.Infrastructure.Authorization.AppViewPermission;
using PlayerApiTeam = Player.Api.Client.Team;
using CoordinateEntity = Player.Vm.Api.Domain.Models.Coordinate;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;
using VmMapEntity = Player.Vm.Api.Domain.Models.VmMap;
using VmMapModel = Player.Vm.Api.Features.Vms.VmMap;
using VmModel = Player.Vm.Api.Features.Vms.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The Vm and map endpoints in process, through the real Startup and against real PostgreSQL.
///
/// <see cref="VmServiceAuthorizationTests"/> already drives every refusing path in the service, so this
/// class deliberately does not repeat the permission matrix. What it adds is everything between the
/// service and the wire, none of which a service-level test can reach: that the routes are where the
/// generated clients expect them, that the query string binds (including what an omitted flag defaults
/// to), that the service's exceptions arrive as status codes rather than 500s, that a 201 carries a
/// Location a caller can follow, and that the two hand-written 404 bodies keep the shape the UI reads.
/// </summary>
/// <remarks>
/// Writes here also exercise the entity-event handlers, which run inside the request and resolve a
/// team's View through <see cref="VmApiFactory.Views"/>. That is why creating a Vm reaches player.api at
/// all.
/// </remarks>
public class VmsEndpointTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        Factory.PlayerApi.ClearSubstitute();
        Factory.PlayerApiClient.ClearSubstitute();
        Factory.Views.ClearSubstitute();
        Factory.AllowEverything();
    }

    #region Reading Vms

    [Fact]
    public async Task GetAll_ReturnsEveryVm()
    {
        var mine = Vm([Guid.NewGuid()]);
        var theirs = Vm([Guid.NewGuid()]);
        await Seed(mine, theirs);

        var vms = await Get<VmModel[]>("/api/vms");

        // Sorted on both sides: nothing behind this route orders its results.
        Assert.Equal(Sorted(mine.Id, theirs.Id), vms.Select(x => x.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Get_ReturnsTheVmWithItsTeamIds()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId], name: "workstation");
        await Seed(vm);
        TeamVisibility(teamId);

        var body = await Get<VmModel>($"/api/vms/{vm.Id}");

        Assert.Equal(vm.Id, body.Id);
        Assert.Equal("workstation", body.Name);
        Assert.Equal<Guid>([teamId], body.TeamIds);
    }

    /// <summary>
    /// A missing Vm is a 404 - but by way of <c>EntityNotFoundException</c> from the service and the
    /// exception middleware, not the controller's own <c>if (vm == null) return NotFound(vm)</c>, which
    /// nothing can reach. Pinned because the two differ on the wire: the middleware writes a
    /// <c>ProblemDetails</c> body and the unreachable branch would write a bare <c>null</c>.
    /// </summary>
    [Fact]
    public async Task Get_ForAnUnknownVm_Is404WithAProblemDetailsBody()
    {
        var response = await Client.GetAsync($"/api/vms/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("\"status\":404", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// Both list routes take <c>includePersonal</c> and <c>onlyMine</c> as nullable bools that the
    /// controller collapses to false. An omitted flag therefore has to hide personal Vms rather than
    /// bind as null and be treated as true somewhere downstream, and the flags have to bind at all -
    /// a renamed query parameter would silently fall back to the default and quietly change what every
    /// caller sees.
    /// </summary>
    [Theory]
    [InlineData("teams/{team}/vms", "", "shared")]
    [InlineData("teams/{team}/vms", "?includePersonal=true", "mine,shared")]
    [InlineData("teams/{team}/vms", "?onlyMine=true", "mine")]
    [InlineData("views/{view}/vms", "", "shared")]
    [InlineData("views/{view}/vms", "?includePersonal=true", "mine,shared")]
    [InlineData("views/{view}/vms", "?onlyMine=true", "mine")]
    public async Task ListRoutes_BindTheirFlags(string template, string query, string expected)
    {
        var teamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();

        var shared = Vm([teamId], name: "shared");
        var mine = Vm([teamId], name: "mine", userId: Factory.UserId);
        var theirs = Vm([teamId], name: "theirs", userId: Guid.NewGuid());
        await Seed(shared, mine, theirs);

        TeamVisibility(teamId);
        View(viewId, teamId);

        var route = "/api/" + template
            .Replace("{team}", teamId.ToString())
            .Replace("{view}", viewId.ToString()) + query;

        var vms = await Get<VmModel[]>(route);

        Assert.Equal(expected.Split(','), vms.Select(x => x.Name).OrderBy(x => x));
    }

    [Theory]
    [InlineData("teams/{team}/vms")]
    [InlineData("views/{view}/vms")]
    public async Task ListRoutes_BindTheNameFilter(string template)
    {
        var teamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        await Seed(Vm([teamId], name: "wanted"), Vm([teamId], name: "other"));

        TeamVisibility(teamId);
        View(viewId, teamId);

        var route = "/api/" + template
            .Replace("{team}", teamId.ToString())
            .Replace("{view}", viewId.ToString()) + "?name=wanted";

        var vms = await Get<VmModel[]>(route);

        Assert.Equal("wanted", Assert.Single(vms).Name);
    }

    [Fact]
    public async Task GetByTeamId_ForATeamTheCallerCannotSee_Is403()
    {
        var teamId = Guid.NewGuid();
        await Seed(Vm([teamId]));

        // Visible to the caller, but not this team: the team must be in its own visibility set.
        Factory.PlayerApi.GetVisibilityContextForTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new VisibilityContext(null, false, [Guid.NewGuid()]));

        var response = await Client.GetAsync($"/api/teams/{teamId}/vms", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// player.api not knowing the View is an empty list here rather than a 404, unlike the map and team
    /// routes below. Pinned because the difference is not obvious from the outside and the VM UI depends
    /// on it: a View being torn down must not make the Vm list read as a broken route.
    /// </summary>
    [Fact]
    public async Task GetByViewId_ForAnUnknownView_IsAnEmptyList()
    {
        var viewId = Guid.NewGuid();
        await Seed(Vm([Guid.NewGuid()]));

        Factory.PlayerApi.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(VisibilityContext.Empty);
        Factory.PlayerApi.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns((IEnumerable<PlayerApiTeam>)null);

        Assert.Empty(await Get<VmModel[]>($"/api/views/{viewId}/vms"));
    }

    #endregion

    #region Vm permissions

    /// <summary>
    /// The endpoint the UI reads to decide which controls to render. It translates player.api's
    /// permission strings into this application's enums, dropping any it does not recognize, and keeps
    /// only the claims belonging to teams the Vm is actually on - so a caller's rights over a team
    /// beside it do not turn into rights over this Vm.
    /// </summary>
    [Fact]
    public async Task GetVmPermissions_KeepsOnlyTheVmsOwnTeamsAndOnlyKnownPermissions()
    {
        var teamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();

        var vm = Vm([teamId]);
        await Seed(vm);
        TeamVisibility(teamId);

        Factory.Views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([viewId]);
        Factory.PlayerApiClient
            .GetMyTeamPermissionsAsync(viewId, null, true, Arg.Any<CancellationToken>())
            .Returns([
                Claim(teamId, nameof(AppTeamPermission.ViewTeam), nameof(AppViewPermission.ViewView), "NotAPermission"),
                Claim(otherTeamId, nameof(AppTeamPermission.ManageTeam), nameof(AppViewPermission.ManageView))
            ]);

        var permissions = await Get<VmPermissionResult>($"/api/vms/{vm.Id}/permissions");

        Assert.Equal([AppTeamPermission.ViewTeam], permissions.TeamPermissions);
        Assert.Equal([AppViewPermission.ViewView], permissions.ViewPermissions);
    }

    [Fact]
    public async Task GetVmPermissions_ForAnUnknownVm_Is404()
    {
        var response = await Client.GetAsync($"/api/vms/{Guid.NewGuid()}/permissions", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Writing Vms

    /// <summary>
    /// 201 with a Location a caller can follow. The route is built by action name, so renaming Get
    /// without updating the CreatedAtAction would leave the response without one.
    /// </summary>
    [Fact]
    public async Task Create_Returns201AndALocationThatResolves()
    {
        var teamId = Guid.NewGuid();
        TeamVisibility(teamId);

        var response = await Client.PostAsJsonAsync("/api/vms", CreateForm(teamId, "new-vm"), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<VmModel>(JsonOptions, Ct);
        var followed = await Get<VmModel>(response.Headers.Location.ToString());

        Assert.Equal(created.Id, followed.Id);
        Assert.Equal("new-vm", followed.Name);
        Assert.Equal<Guid>([teamId], followed.TeamIds);
    }

    /// <summary>
    /// Name is <c>[Required]</c>, and <c>[ApiController]</c> turns an invalid model into a 400 before the
    /// action body runs. Worth pinning because the action also throws a bare
    /// <c>InvalidOperationException</c> on the same condition, which would surface as a 500: if the
    /// automatic filter were ever suppressed, this test is what would notice the 400 becoming one.
    /// </summary>
    [Fact]
    public async Task Create_WithoutAName_Is400()
    {
        var form = CreateForm(Guid.NewGuid(), "unused");
        form.Name = null;

        var response = await Client.PostAsJsonAsync("/api/vms", form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutManageOnTheTeam_Is403()
    {
        Factory.PlayerApi.CanManageTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await Client.PostAsJsonAsync(
            "/api/vms", CreateForm(Guid.NewGuid(), "new-vm"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_ReplacesTheVm()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId], name: "before");
        await Seed(vm);

        var response = await Client.PutAsJsonAsync(
            $"/api/vms/{vm.Id}", new VmUpdateForm { Name = "after", Url = "https://console/after" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<VmModel>(JsonOptions, Ct);
        Assert.Equal("after", body.Name);

        await using var context = NewContext();
        Assert.Equal("after", context.Vms.Single(x => x.Id == vm.Id).Name);
    }

    [Fact]
    public async Task Update_ForAnUnknownVm_Is404()
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/vms/{Guid.NewGuid()}", new VmUpdateForm { Name = "after" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns204AndRemovesTheRow()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);

        var response = await Client.DeleteAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == vm.Id));
    }

    [Fact]
    public async Task Delete_ForAnUnknownVm_Is404()
    {
        var response = await Client.DeleteAsync($"/api/vms/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Adding answers 200 while removing answers 204. The asymmetry is not a mistake to fix here - it is
    /// what the generated clients were built against - so it is pinned rather than corrected.
    /// </summary>
    [Fact]
    public async Task AddToTeam_Returns200AndAddsTheRow()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var teamId = Guid.NewGuid();

        var response = await Client.PostAsync($"/api/teams/{teamId}/vms/{vm.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.VmTeams.Where(x => x.VmId == vm.Id && x.TeamId == teamId));
    }

    // Idempotent: the second call is the same 200 and does not add a duplicate row.
    [Fact]
    public async Task AddToTeam_RepeatedForTheSameTeam_IsStill200AndAddsNothing()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId]);
        await Seed(vm);

        var response = await Client.PostAsync($"/api/teams/{teamId}/vms/{vm.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.VmTeams.Where(x => x.VmId == vm.Id));
    }

    [Fact]
    public async Task AddToTeam_ForAnUnknownVm_Is404()
    {
        var response = await Client.PostAsync(
            $"/api/teams/{Guid.NewGuid()}/vms/{Guid.NewGuid()}", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemoveFromTeam_Returns204AndRemovesTheRow()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId, Guid.NewGuid()]);
        await Seed(vm);

        var response = await Client.DeleteAsync($"/api/teams/{teamId}/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.VmTeams.Where(x => x.VmId == vm.Id && x.TeamId == teamId));
    }

    // A Vm with no team at all would be unreachable by every list route and invisible to authorization.
    [Fact]
    public async Task RemoveFromTeam_WhenItIsTheOnlyTeam_Is403AndLeavesTheRow()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId]);
        await Seed(vm);

        var response = await Client.DeleteAsync($"/api/teams/{teamId}/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.VmTeams.Where(x => x.VmId == vm.Id));
    }

    #endregion

    #region Maps

    [Fact]
    public async Task CreateMap_Returns201AndALocationThatResolves()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        View(viewId, teamId);

        var form = new VmMapCreateForm
        {
            Name = "floor-1",
            ImageUrl = "https://images/floor-1.png",
            TeamIds = [teamId],
            Coordinates = [Coordinate("desk", 1.5, 2.5)]
        };

        var response = await Client.PostAsJsonAsync($"/api/views/{viewId}/map", form, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var followed = await Get<VmMapModel>(response.Headers.Location.ToString());

        Assert.Equal("floor-1", followed.Name);
        Assert.Equal(viewId, followed.ViewId);
        Assert.Equal<Guid>([teamId], followed.TeamIds);
    }

    // The view has to exist in player.api before a map can be hung off it.
    [Fact]
    public async Task CreateMap_ForAViewPlayerApiDoesNotKnow_Is403()
    {
        var viewId = Guid.NewGuid();
        Factory.PlayerApi.GetViewByIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns<View>(_ => throw new ApiException(
                "View not found", (int)HttpStatusCode.NotFound, null, null, null));

        var response = await Client.PostAsJsonAsync(
            $"/api/views/{viewId}/map", new VmMapCreateForm { Name = "floor-1", TeamIds = [] }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAllMaps_ReturnsEveryMap()
    {
        var mine = Map([Guid.NewGuid()]);
        var theirs = Map([Guid.NewGuid()]);
        await Seed(mine, theirs);

        var maps = await Get<VmMapModel[]>("/api/views/maps");

        Assert.Equal(Sorted(mine.Id, theirs.Id), maps.Select(x => x.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task GetViewMaps_ReturnsTheViewsMaps()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var map = Map([teamId], viewId);
        await Seed(map, Map([Guid.NewGuid()]));
        View(viewId, teamId);

        var maps = await Get<VmMapModel[]>($"/api/views/maps/viewMaps/{viewId}");

        Assert.Equal(map.Id, Assert.Single(maps).Id);
    }

    /// <summary>
    /// This route and <c>views/{viewId}/teams</c> hand-write their 404 body as
    /// <c>{ title, status }</c> rather than letting the middleware produce a ProblemDetails. The VM UI
    /// reads <c>title</c>, so the shape is asserted and not just the status.
    /// </summary>
    [Fact]
    public async Task GetViewMaps_ForAViewPlayerApiDoesNotKnow_Is404WithATitle()
    {
        var viewId = Guid.NewGuid();
        Factory.PlayerApi.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(VisibilityContext.Empty);
        Factory.PlayerApi.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns((IEnumerable<PlayerApiTeam>)null);

        var response = await Client.GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("\"title\":\"View not found\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task GetMap_ReturnsTheMapWithItsCoordinates()
    {
        var map = Map([Guid.NewGuid()]);
        map.Coordinates = [CoordinateEntityFor("desk", 1.5, 2.5)];
        await Seed(map);

        var body = await Get<VmMapModel>($"/api/views/maps/{map.Id}");

        var coordinate = Assert.Single(body.Coordinates);
        Assert.Equal("desk", coordinate.Label);
        Assert.Equal(1.5, coordinate.XPosition);
        Assert.Equal<string>(["https://console/desk"], coordinate.Urls);
    }

    [Fact]
    public async Task GetMap_ForAnUnknownMap_Is404()
    {
        var response = await Client.GetAsync($"/api/views/maps/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTeamMap_ReturnsTheTeamsMap()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map, Map([Guid.NewGuid()]));
        Factory.PlayerApi.IsTeamVisibleAsync(teamId, Arg.Any<CancellationToken>()).Returns(true);

        Assert.Equal(map.Id, (await Get<VmMapModel>($"/api/teams/{teamId}/map")).Id);
    }

    [Fact]
    public async Task GetTeamMap_WhenTheTeamHasNoMap_Is404()
    {
        var teamId = Guid.NewGuid();
        await Seed(Map([Guid.NewGuid()]));
        Factory.PlayerApi.IsTeamVisibleAsync(teamId, Arg.Any<CancellationToken>()).Returns(true);

        var response = await Client.GetAsync($"/api/teams/{teamId}/map", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Unstubbed IsTeamVisibleAsync answers false, which is the denial this asserts.
    [Fact]
    public async Task GetTeamMap_ForATeamTheCallerCannotSee_Is403()
    {
        var teamId = Guid.NewGuid();
        await Seed(Map([teamId]));

        var response = await Client.GetAsync($"/api/teams/{teamId}/map", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The coordinates are cleared and rebuilt from the form, so an update is a replacement rather than
    /// a merge. Asserted through a re-read: the response could be right while the rows underneath were
    /// left orphaned.
    /// </summary>
    [Fact]
    public async Task UpdateMap_ReplacesTheCoordinates()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        map.Coordinates = [CoordinateEntityFor("before", 1, 1)];
        await Seed(map);
        View(map.ViewId, teamId);

        var form = new VmMapUpdateForm
        {
            Name = "after",
            ImageUrl = map.ImageUrl,
            TeamIds = [teamId],
            Coordinates = [Coordinate("after", 9, 9)]
        };

        var response = await Client.PutAsJsonAsync($"/api/views/maps/{map.Id}", form, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        var stored = await context.Maps
            .Include(x => x.Coordinates)
            .SingleAsync(x => x.Id == map.Id, Ct);

        Assert.Equal("after", stored.Name);
        Assert.Equal("after", Assert.Single(stored.Coordinates).Label);
    }

    [Fact]
    public async Task UpdateMap_ForAnUnknownMap_Is404()
    {
        var form = new VmMapUpdateForm { Name = "after", TeamIds = [], Coordinates = [] };

        var response = await Client.PutAsJsonAsync($"/api/views/maps/{Guid.NewGuid()}", form, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMap_Returns204AndRemovesTheRow()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);

        var response = await Client.DeleteAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Maps.Where(x => x.Id == map.Id));
    }

    [Fact]
    public async Task DeleteMap_ForAnUnknownMap_Is404()
    {
        var response = await Client.DeleteAsync($"/api/views/maps/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMap_WithoutManageOnItsTeams_Is403AndLeavesTheRow()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        Factory.PlayerApi.CanManageTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await Client.DeleteAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.Maps.Where(x => x.Id == map.Id));
    }

    #endregion

    #region Teams

    // Exists so the VM UI does not have to call player.api itself, so the projection down to id and name
    // is the contract.
    [Fact]
    public async Task GetTeams_ReturnsTheViewsTeamsAsIdAndName()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        View(viewId, teamId);

        var teams = await Get<SimpleTeam[]>($"/api/views/{viewId}/teams");

        var team = Assert.Single(teams);
        Assert.Equal(teamId, team.Id);
        Assert.Equal($"team-{teamId}", team.Name);
    }

    [Fact]
    public async Task GetTeams_ForAViewPlayerApiDoesNotKnow_Is404WithATitle()
    {
        var viewId = Guid.NewGuid();
        Factory.PlayerApi.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns((IEnumerable<PlayerApiTeam>)null);

        var response = await Client.GetAsync($"/api/views/{viewId}/teams", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("\"title\":\"View not found\"", await response.Content.ReadAsStringAsync(Ct));
    }

    #endregion

    #region Authorization

    /// <summary>
    /// Every route on the controller is behind the default authorization policy, which the substituted
    /// player.api has no say in. Driven as a theory because the attribute is on the class: a route added
    /// to a controller that had lost its <c>[Authorize]</c> would answer anonymously, and only a check
    /// per route notices. The five bulk power routes are covered the same way in
    /// <see cref="BulkPowerOperationEndpointTests"/>.
    /// </summary>
    [Theory]
    [InlineData("GET", "vms")]
    [InlineData("GET", "vms/{id}")]
    [InlineData("GET", "vms/{id}/permissions")]
    [InlineData("GET", "teams/{id}/vms")]
    [InlineData("GET", "views/{id}/vms")]
    [InlineData("POST", "vms")]
    [InlineData("PUT", "vms/{id}")]
    [InlineData("DELETE", "vms/{id}")]
    [InlineData("POST", "teams/{id}/vms/{id}")]
    [InlineData("DELETE", "teams/{id}/vms/{id}")]
    [InlineData("POST", "views/{id}/map")]
    [InlineData("GET", "views/maps")]
    [InlineData("GET", "views/maps/viewMaps/{id}")]
    [InlineData("GET", "views/maps/{id}")]
    [InlineData("PUT", "views/maps/{id}")]
    [InlineData("DELETE", "views/maps/{id}")]
    [InlineData("GET", "teams/{id}/map")]
    [InlineData("GET", "views/{id}/teams")]
    public async Task EveryRoute_RejectsAnUnauthenticatedRequest(string method, string template)
    {
        var route = "/api/" + template.Replace("{id}", Guid.NewGuid().ToString());

        using var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = JsonContent.Create(new { name = "anything", teamIds = Array.Empty<Guid>() })
        };

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The system-wide reads are gated on a system permission of their own, not on team visibility.
    [Theory]
    [InlineData("vms")]
    [InlineData("views/maps")]
    public async Task SystemWideReads_WithoutASystemPermission_Are403(string route)
    {
        await Seed(Vm([Guid.NewGuid()]), Map([Guid.NewGuid()]));
        DenyEverything();

        var response = await Client.GetAsync($"/api/{route}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// player.api answering 404 for a view is a 404 here, not a 500: the middleware unwraps
    /// <c>Player.Api.Client.ApiException</c> for that one status. A caller asking about a view that has
    /// been deleted is the ordinary case, and it must not read as this API being broken.
    /// </summary>
    [Fact]
    public async Task Get_WhenPlayerApiReportsTheTeamIsGone_Is404()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);

        Factory.PlayerApi.CanViewTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ApiException(
                "Team not found", (int)HttpStatusCode.NotFound, null, null, null));

        var response = await Client.GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Any other failure from player.api is a 500. Reporting an outage as 403 would send a user to their
    // administrator instead of to whoever can restart player.api.
    [Fact]
    public async Task Get_WhenPlayerApiIsUnreachable_Is500()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);

        Factory.PlayerApi.CanViewTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ApiException(
                "Service Unavailable", (int)HttpStatusCode.ServiceUnavailable, null, null, null));

        var response = await Client.GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    #endregion

    #region Helpers

    private static IEnumerable<Guid> Sorted(params Guid[] ids) => ids.OrderBy(x => x);

    private async Task<T> Get<T>(string route)
    {
        var response = await Client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    private void DenyEverything()
    {
        Factory.PlayerApi.CanViewTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(false);
        Factory.PlayerApi.CanEditTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(false);
        Factory.PlayerApi.CanManageTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(false);
        Factory.PlayerApi
            .Can(default, default, default, default, default, Ct)
            .ReturnsForAnyArgs(false);
    }

    /// <summary>Makes <paramref name="teamId"/> visible to the caller as its own primary team.</summary>
    private void TeamVisibility(Guid teamId) =>
        Factory.PlayerApi.GetVisibilityContextForTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new VisibilityContext(teamId, false, [teamId]));

    /// <summary>
    /// Wires the four things a View-scoped route asks player.api for: the caller's visibility, the
    /// View's teams, the View itself and each team by id.
    /// </summary>
    private void View(Guid viewId, params Guid[] teamIds)
    {
        Factory.PlayerApi.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(new VisibilityContext(
                teamIds.Length == 0 ? null : teamIds[0], false, [.. teamIds]));
        Factory.PlayerApi.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(teamIds.Select(x => PlayerTeam(x, viewId)).ToArray());
        Factory.PlayerApi.GetViewByIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(new View { Id = viewId, Name = $"view-{viewId}" });

        foreach (var teamId in teamIds)
        {
            Factory.PlayerApi.GetTeamById(teamId).Returns(PlayerTeam(teamId, viewId));
        }
    }

    private static PlayerApiTeam PlayerTeam(Guid id, Guid viewId) =>
        new() { Id = id, Name = $"team-{id}", ViewId = viewId, IsPrimary = true };

    private static TeamPermissionsClaim Claim(Guid teamId, params string[] permissionValues) =>
        new() { TeamId = teamId, PermissionValues = permissionValues, DirectPermissionValues = [] };

    private static VmCreateForm CreateForm(Guid teamId, string name) =>
        new() { Id = Guid.NewGuid(), Name = name, TeamIds = [teamId] };

    private static CoordinateCreateForm Coordinate(string label, double x, double y) =>
        new()
        {
            Label = label,
            XPosition = x,
            YPosition = y,
            Radius = 10,
            Urls = [$"https://console/{label}"]
        };

    private static CoordinateEntity CoordinateEntityFor(string label, double x, double y) =>
        new()
        {
            Label = label,
            XPosition = x,
            YPosition = y,
            Radius = 10,
            Urls = [$"https://console/{label}"]
        };

    private static VmEntity Vm(Guid[] teamIds, string name = null, Guid? userId = null)
    {
        var id = Guid.NewGuid();

        return new VmEntity
        {
            Id = id,
            Name = name ?? $"vm-{id}",
            Type = VmType.Vsphere,
            UserId = userId,
            VmTeams = [.. teamIds.Select(teamId => new VmTeam(teamId, id))]
        };
    }

    private static VmMapEntity Map(Guid[] teamIds, Guid? viewId = null) =>
        new()
        {
            Name = "map",
            ImageUrl = "https://images/map.png",
            ViewId = viewId ?? Guid.NewGuid(),
            TeamIds = [.. teamIds]
        };

    #endregion
}
