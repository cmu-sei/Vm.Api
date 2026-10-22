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
using NSubstitute.ClearExtensions;
using Player.Api.Client;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Tests.Support;
using AppSystemPermission = Player.Vm.Api.Infrastructure.Authorization.AppSystemPermission;
using AppTeamPermission = Player.Vm.Api.Infrastructure.Authorization.AppTeamPermission;
using AppViewPermission = Player.Vm.Api.Infrastructure.Authorization.AppViewPermission;
using PlayerApiTeam = Player.Api.Client.Team;
using CoordinateEntity = Player.Vm.Api.Domain.Models.Coordinate;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;
using VmMapEntity = Player.Vm.Api.Domain.Models.VmMap;
using VmMapModel = Player.Vm.Api.Features.Vms.VmMap;
using VmModel = Player.Vm.Api.Features.Vms.Vm;

namespace Player.Vm.Api.Tests.Features.Vms;

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
/// team's View through <see cref="VmAppFactory.Views"/>. That is why creating a Vm reaches player.api at
/// all.
/// </remarks>
public class VmsEndpointTests(DatabaseFixture fixture, VmAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmAppFactory>
{
    /// <summary>A numeric VM type not declared by ProxmoxVmType.</summary>
    private const ProxmoxVmType UndefinedProxmoxType = (ProxmoxVmType)999;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

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
        var response = await RootClient.GetAsync($"/api/vms/{Guid.NewGuid()}", Ct);

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
        var mine = Vm([teamId], name: "mine", userId: Root.Id);
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

        // The team is in a View where the caller has no primary team, so it sees none of its teams.
        Factory.Player.PlaceTeam(teamId, Guid.NewGuid());

        var response = await RootClient.GetAsync($"/api/teams/{teamId}/vms", Ct);

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

        // A View player.api does not have: its teams route answers 404.

        Assert.Empty(await Get<VmModel[]>($"/api/views/{viewId}/vms"));
    }

    // Personal Vms included, and no query flags to ask for them: the caller is not on the teams, so
    // there is no "mine" to narrow to.
    [Fact]
    public async Task GetAllByViewId_ReturnsEveryVmOnTheViewsTeams()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        await Seed(
            Vm([teamId], name: "shared"),
            Vm([teamId], name: "theirs", userId: Guid.NewGuid()),
            Vm([Guid.NewGuid()], name: "elsewhere"));
        Roster(viewId, teamId);

        var vms = await Get<VmModel[]>($"/api/views/{viewId}/vms/all");

        Assert.Equal(["shared", "theirs"], vms.Select(x => x.Name).OrderBy(x => x));
    }

    #endregion

    #region Vm permissions

    /// <summary>
    /// The endpoint the UI reads to decide which controls to render. It translates player.api's
    /// permission strings into this application's enums, dropping any it does not recognize, and keeps
    /// only the claims belonging to teams the Vm is actually on - so a caller's rights over a team
    /// beside it do not turn into rights over this Vm. System permissions are reported alongside them
    /// because a system-wide grant such as ControlVms reaches every Vm without any team claim.
    /// </summary>
    [Fact]
    public async Task GetVmPermissions_KeepsOnlyTheVmsOwnTeamsAndOnlyKnownPermissions()
    {
        var teamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();

        var vm = Vm([teamId]);
        await Seed(vm);

        Factory.Views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([viewId]);
        var actor = await Actor()
            .InView(teamId, viewId).InView(otherTeamId, viewId)
            .Holding(AppSystemPermission.ControlVms)
            .SeedAsync();

        // Claims as player.api reports them, none of them direct: what the endpoint keeps is decided by
        // which team each is on and whether vm.api knows the permission.
        var caller = Factory.Player.Caller(actor.Id);
        caller.ClaimOn(teamId).Permissions.UnionWith([nameof(AppTeamPermission.ViewTeam), nameof(AppViewPermission.ViewView), "NotAPermission"]);
        caller.ClaimOn(otherTeamId).Permissions.UnionWith([nameof(AppTeamPermission.ManageTeam), nameof(AppViewPermission.ManageView)]);

        var permissions = await ReadAsync<VmPermissionResult>(await Client(actor).GetAsync($"/api/vms/{vm.Id}/permissions", Ct), JsonOptions);

        Assert.Equal([AppTeamPermission.ViewTeam], permissions.TeamPermissions);
        Assert.Equal([AppViewPermission.ViewView], permissions.ViewPermissions);
        Assert.Equal([AppSystemPermission.ControlVms], permissions.SystemPermissions);
    }

    [Fact]
    public async Task GetVmPermissions_ForAnUnknownVm_Is404()
    {
        var response = await RootClient.GetAsync($"/api/vms/{Guid.NewGuid()}/permissions", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Writing Vms

    [Fact]
    public async Task Create_with_a_null_id_answers_a_server_error_and_stores_nothing()
    {
        var form = CreateForm(Guid.NewGuid(), "null-id-vm");
        form.Id = null;

        var response = await RootClient.PostAsJsonAsync("/api/vms", form, Ct);

        var problem = await AssertJsonError(HttpStatusCode.InternalServerError, response);
        Assert.Contains("Error mapping types.", problem.Detail);
        Assert.Contains("Destination Member:", problem.Detail);
        Assert.Contains("VmTeams", problem.Detail);
        await using var context = NewContext();
        Assert.Empty(context.Vms);
        Assert.Empty(context.VmTeams);
    }

    [Theory]
    [InlineData("POST", "/api/vms", HttpStatusCode.Created, 101)]
    [InlineData("PUT", "/api/vms/{id}", HttpStatusCode.OK, 100)]
    public async Task Write_stores_and_returns_a_Proxmox_type_outside_the_enum(
        string method, string route, HttpStatusCode status, int vmid)
    {
        var teamId = Guid.NewGuid();
        TeamVisibility(teamId);
        var vm = TestData.ProxmoxVm(teamId);
        await Seed(vm);
        var form = CreateForm(teamId, "proxmox-vm");
        form.ProxmoxVmInfo = new Player.Vm.Api.Features.Vms.ProxmoxVmInfo
        {
            Id = vmid,
            Node = "node-1",
            Type = UndefinedProxmoxType
        };
        using var request = new HttpRequestMessage(new HttpMethod(method), route.Replace("{id}", vm.Id.ToString()))
        {
            Content = JsonContent.Create(form)
        };

        var response = await RootClient.SendAsync(request, Ct);

        await AssertStatus(status, response);
        var body = await response.Content.ReadFromJsonAsync<VmModel>(JsonOptions, Ct);
        Assert.Equal(VmType.Proxmox, body.Type);
        Assert.Equal(UndefinedProxmoxType, body.ProxmoxVmInfo.Type);
        await using var context = NewContext();
        var stored = context.Vms.Include(x => x.ProxmoxVmInfo).Single(x => x.Id == body.Id);
        Assert.Equal(VmType.Proxmox, stored.Type);
        Assert.Equal(UndefinedProxmoxType, stored.ProxmoxVmInfo.Type);
        Assert.Equal(vmid, stored.ProxmoxVmInfo.Id);
        Assert.Equal("node-1", stored.ProxmoxVmInfo.Node);
    }

    [Fact]
    public async Task Create_with_an_existing_Proxmox_id_answers_a_server_error_and_keeps_the_vm()
    {
        var teamId = Guid.NewGuid();
        TeamVisibility(teamId);
        var vm = TestData.ProxmoxVm(teamId);
        await Seed(vm);
        var form = CreateForm(teamId, "another-vm");
        form.ProxmoxVmInfo = new Player.Vm.Api.Features.Vms.ProxmoxVmInfo
        {
            Id = vm.ProxmoxVmInfo.Id,
            Node = "another-node",
            Type = ProxmoxVmType.QEMU
        };

        var response = await RootClient.PostAsJsonAsync("/api/vms", form, Ct);

        var problem = await AssertJsonError(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", problem.Detail);
        await using var context = NewContext();
        Assert.Equal(vm.Id, Assert.Single(context.Vms).Id);
        var info = Assert.Single(context.ProxmoxVmInfo);
        Assert.Equal(vm.ProxmoxVmInfo.Id, info.Id);
        Assert.Equal(vm.Id, info.VmId);
        Assert.Equal("pve-1", info.Node);
    }

    [Fact]
    public async Task Update_adding_Proxmox_info_to_a_vm_answers_a_server_error_and_keeps_the_vm()
    {
        var vm = Vm([Guid.NewGuid()], name: "before");
        await Seed(vm);
        var form = new VmUpdateForm
        {
            Name = "after",
            ProxmoxVmInfo = new Player.Vm.Api.Features.Vms.ProxmoxVmInfo
            {
                Id = 100,
                Node = "node-1",
                Type = ProxmoxVmType.QEMU
            }
        };

        var response = await RootClient.PutAsJsonAsync($"/api/vms/{vm.Id}", form, Ct);

        var problem = await AssertJsonError(HttpStatusCode.InternalServerError, response);
        Assert.StartsWith("The database operation was expected to affect 1 row(s), but actually affected 0 row(s)", problem.Detail);
        await using var context = NewContext();
        var stored = context.Vms.Single(x => x.Id == vm.Id);
        Assert.Equal("before", stored.Name);
        Assert.Equal(VmType.Vsphere, stored.Type);
        Assert.Empty(context.ProxmoxVmInfo);
    }

    [Fact]
    public async Task Create_with_a_repeated_team_answers_a_server_error_and_stores_nothing()
    {
        var teamId = Guid.NewGuid();
        TeamVisibility(teamId);
        var form = CreateForm(teamId, "repeated-team-vm");
        form.TeamIds = [teamId, teamId];

        var response = await RootClient.PostAsJsonAsync("/api/vms", form, Ct);

        var problem = await AssertJsonError(HttpStatusCode.InternalServerError, response);
        Assert.Equal("A server error occurred.", problem.Title);
        Assert.Contains("The instance of entity type 'VmTeam' cannot be tracked", problem.Detail);
        await using var context = NewContext();
        Assert.Empty(context.Vms);
        Assert.Empty(context.VmTeams);
    }

    /// <summary>
    /// 201 with a Location a caller can follow. The route is built by action name, so renaming Get
    /// without updating the CreatedAtAction would leave the response without one.
    /// </summary>
    [Fact]
    public async Task Create_Returns201AndALocationThatResolves()
    {
        var teamId = Guid.NewGuid();
        TeamVisibility(teamId);

        var response = await RootClient.PostAsJsonAsync("/api/vms", CreateForm(teamId, "new-vm"), Ct);

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

        var response = await RootClient.PostAsJsonAsync("/api/vms", form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_ReplacesTheVm()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId], name: "before");
        await Seed(vm);

        var response = await RootClient.PutAsJsonAsync(
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
        var response = await RootClient.PutAsJsonAsync(
            $"/api/vms/{Guid.NewGuid()}", new VmUpdateForm { Name = "after" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns204AndRemovesTheRow()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);

        var response = await RootClient.DeleteAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == vm.Id));
    }

    [Fact]
    public async Task Delete_ForAnUnknownVm_Is404()
    {
        var response = await RootClient.DeleteAsync($"/api/vms/{Guid.NewGuid()}", Ct);

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

        var response = await RootClient.PostAsync($"/api/teams/{teamId}/vms/{vm.Id}", null, Ct);

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

        var response = await RootClient.PostAsync($"/api/teams/{teamId}/vms/{vm.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.VmTeams.Where(x => x.VmId == vm.Id));
    }

    [Fact]
    public async Task AddToTeam_ForAnUnknownVm_Is404()
    {
        var response = await RootClient.PostAsync(
            $"/api/teams/{Guid.NewGuid()}/vms/{Guid.NewGuid()}", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemoveFromTeam_Returns204AndRemovesTheRow()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId, Guid.NewGuid()]);
        await Seed(vm);

        var response = await RootClient.DeleteAsync($"/api/teams/{teamId}/vms/{vm.Id}", Ct);

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

        var response = await RootClient.DeleteAsync($"/api/teams/{teamId}/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.VmTeams.Where(x => x.VmId == vm.Id));
    }

    #endregion

    #region Maps

    [Fact]
    public async Task CreateMap_with_an_existing_coordinate_id_answers_a_server_error_and_keeps_the_map()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        map.Coordinates = [CoordinateEntityFor("original", 1.5, 2.5)];
        await Seed(map);
        View(map.ViewId, teamId);
        var coordinateId = map.Coordinates.Single().Id;
        var form = MapCreateForm(teamId);
        var coordinate = Coordinate("replacement", 3, 4);
        coordinate.Id = coordinateId;
        form.Coordinates = [coordinate];

        var response = await RootClient.PostAsJsonAsync($"/api/views/{map.ViewId}/map", form, Ct);

        var problem = await AssertJsonError(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", problem.Detail);
        await using var context = NewContext();
        var stored = Assert.Single(context.Maps.Include(x => x.Coordinates));
        Assert.Equal(map.Id, stored.Id);
        Assert.Equal("map", stored.Name);
        Assert.Equal(map.ViewId, stored.ViewId);
        Assert.Equal(map.ImageUrl, stored.ImageUrl);
        Assert.Equal<Guid>([teamId], stored.TeamIds);
        Assert.Single(context.Set<CoordinateEntity>());
        var original = Assert.Single(stored.Coordinates);
        Assert.Equal(coordinateId, original.Id);
        Assert.Equal("original", original.Label);
        Assert.Equal(1.5, original.XPosition);
        Assert.Equal(2.5, original.YPosition);
        Assert.Equal(10, original.Radius);
        Assert.Equal(["https://console/original"], original.Urls);
    }

    // Same case as CreateMap_with_an_existing_coordinate_id_answers_a_server_error_and_keeps_the_map.
    [Fact]
    public async Task CreateMap_with_a_repeated_coordinate_id_answers_a_server_error_and_stores_nothing()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        View(viewId, teamId);
        var first = Coordinate("first", 1, 2);
        var second = Coordinate("second", 3, 4);
        first.Id = Guid.NewGuid();
        second.Id = first.Id;
        var form = MapCreateForm(teamId);
        form.Coordinates = [first, second];

        var response = await RootClient.PostAsJsonAsync($"/api/views/{viewId}/map", form, Ct);

        var problem = await AssertJsonError(HttpStatusCode.InternalServerError, response);
        Assert.Contains("The instance of entity type 'Coordinate' cannot be tracked", problem.Detail);
        await using var context = NewContext();
        Assert.Empty(context.Maps);
        Assert.Empty(context.Set<CoordinateEntity>());
    }

    [Fact]
    public async Task CreateMap_with_an_existing_id_answers_a_server_error_and_keeps_the_map()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        View(map.ViewId, teamId);
        var form = MapCreateForm(teamId);
        form.Id = map.Id;

        var response = await RootClient.PostAsJsonAsync($"/api/views/{map.ViewId}/map", form, Ct);

        var problem = await AssertJsonError(HttpStatusCode.InternalServerError, response);
        Assert.Equal("A server error occurred.", problem.Title);
        Assert.Contains("The instance of entity type 'VmMap' cannot be tracked", problem.Detail);
        Assert.Contains("another instance with the same key value for {'Id'} is already being tracked", problem.Detail);
        await using var context = NewContext();
        var stored = Assert.Single(context.Maps);
        Assert.Equal(map.Id, stored.Id);
        Assert.Equal("map", stored.Name);
        Assert.Equal(map.ViewId, stored.ViewId);
        Assert.Equal<Guid>([teamId], stored.TeamIds);
    }

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

        var response = await RootClient.PostAsJsonAsync($"/api/views/{viewId}/map", form, Ct);

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
        // A View player.api does not have: its view route answers 404.

        var response = await RootClient.PostAsJsonAsync(
            $"/api/views/{viewId}/map", new VmMapCreateForm { Name = "floor-1", TeamIds = [] }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAllMaps_is_allowed_for_a_caller_holding_exactly_ViewMaps()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(AppSystemPermission.ViewMaps).SeedAsync();

        var response = await Client(actor).GetAsync("/api/views/maps", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Select(x => x.Id));
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

    [Fact]
    public async Task GetViewMaps_includes_the_teamless_maps_for_a_caller_holding_exactly_ViewViewMaps_on_the_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var teamless = Map([], viewId);
        var teamScoped = Map([teamId], viewId);
        await Seed(teamless, teamScoped);
        var actor = await Actor().Holding(AppViewPermission.ViewViewMaps, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            Sorted(teamless.Id, teamScoped.Id),
            (await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Select(x => x.Id).OrderBy(x => x));
    }

    // Only a team's map, so the team-map filter decides alone; the system Map permission answers for every team.
    [Theory]
    [InlineData(AppSystemPermission.ViewMaps)]
    [InlineData(AppSystemPermission.ManageMaps)]
    public async Task GetViewMaps_includes_a_teams_map_for_a_caller_holding_exactly_a_system_Map_read_permission(AppSystemPermission permission)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId], viewId);
        await Seed(map);
        var actor = await Actor().Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Id);
    }

    [Theory]
    [InlineData(AppViewPermission.ViewViewMaps)]
    [InlineData(AppViewPermission.ManageViewMaps)]
    public async Task GetViewMaps_includes_a_teams_map_for_a_caller_holding_exactly_a_Map_read_permission_on_the_view(AppViewPermission permission)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId], viewId);
        await Seed(map);
        var actor = await Actor().Holding(permission, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetViewMaps_includes_a_teams_map_for_a_caller_holding_exactly_ManageTeamMaps_on_the_team()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId], viewId);
        await Seed(map);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeamMaps, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetViewMaps_leaves_out_the_maps_of_a_team_for_a_caller_holding_only_the_system_ViewVms()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Map([teamId], viewId));
        var actor = await Actor().Holding(AppSystemPermission.ViewVms).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct));
    }

    [Fact]
    public async Task GetViewMaps_leaves_out_the_maps_of_a_team_for_a_caller_holding_only_ViewViewVms_on_the_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Map([teamId], viewId));
        var actor = await Actor().Holding(AppViewPermission.ViewViewVms, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct));
    }

    [Fact]
    public async Task GetViewMaps_leaves_out_the_maps_of_a_team_for_a_caller_holding_ViewTeamMaps_only_on_another_team()
    {
        var (viewId, teamId, otherTeam) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Seed(Map([teamId], viewId));
        View(viewId, teamId, otherTeam);
        var actor = await Actor().Holding(AppTeamPermission.ViewTeamMaps, otherTeam).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct));
    }

    [Fact]
    public async Task GetViewMaps_leaves_out_the_teamless_maps_for_a_caller_holding_ViewViewMaps_only_in_another_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var teamScoped = Map([teamId], viewId);
        await Seed(Map([], viewId), teamScoped);
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamMaps, teamId)
            .Holding(AppViewPermission.ViewViewMaps, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(teamScoped.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Id);
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
        // A View player.api does not have: its teams route answers 404.

        var response = await RootClient.GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("\"title\":\"View not found\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task GetAllViewMaps_ReturnsEveryMapInTheViewWithItsCoordinates()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var map = Map([teamId], viewId);
        map.Coordinates = [CoordinateEntityFor("desk", 1.5, 2.5)];
        await Seed(map, Map([Guid.NewGuid()]));
        Roster(viewId, teamId);

        var maps = await Get<VmMapModel[]>($"/api/views/{viewId}/maps/all");

        Assert.Equal("desk", Assert.Single(Assert.Single(maps).Coordinates).Label);
    }

    [Fact]
    public async Task GetAllViewMaps_is_allowed_for_a_caller_holding_exactly_ViewViewMaps_on_the_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId], viewId);
        await Seed(map);
        Roster(viewId, teamId);
        var actor = await Actor().Holding(AppViewPermission.ViewViewMaps, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/maps/all", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Id);
    }

    // The same hand-written 404 body as the View's own listings, for the same reason.
    [Theory]
    [InlineData("vms/all")]
    [InlineData("maps/all")]
    public async Task AllInViewReads_ForAViewPlayerApiDoesNotKnow_Are404WithATitle(string route)
    {
        var viewId = Guid.NewGuid();
        Factory.Views.GetTeamsForView(viewId, true, Arg.Any<CancellationToken>())
            .Returns((List<Guid>)null);

        var response = await RootClient.GetAsync($"/api/views/{viewId}/{route}", Ct);

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
    public async Task GetMap_is_allowed_for_a_caller_holding_exactly_ViewTeamMaps_on_the_maps_team()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        var actor = await Actor().Holding(AppTeamPermission.ViewTeamMaps, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetMap_is_forbidden_for_a_caller_holding_ViewTeamMaps_only_on_another_team()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(AppTeamPermission.ViewTeamMaps, Guid.NewGuid()).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal("You do not have access to this map", (await AssertJsonError(HttpStatusCode.Forbidden, response)).Title);
    }

    [Fact]
    public async Task GetMap_ForAnUnknownMap_Is404()
    {
        var response = await RootClient.GetAsync($"/api/views/maps/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTeamMap_ReturnsTheTeamsMap()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map, Map([Guid.NewGuid()]));

        Assert.Equal(map.Id, (await Get<VmMapModel>($"/api/teams/{teamId}/map")).Id);
    }

    [Fact]
    public async Task GetTeamMap_WhenTheTeamHasNoMap_Is404()
    {
        var teamId = Guid.NewGuid();
        await Seed(Map([Guid.NewGuid()]));

        var response = await RootClient.GetAsync($"/api/teams/{teamId}/map", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        var response = await RootClient.PutAsJsonAsync($"/api/views/maps/{map.Id}", form, Ct);

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

        var response = await RootClient.PutAsJsonAsync($"/api/views/maps/{Guid.NewGuid()}", form, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMap_Returns204AndRemovesTheRow()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);

        var response = await RootClient.DeleteAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Maps.Where(x => x.Id == map.Id));
    }

    [Fact]
    public async Task DeleteMap_ForAnUnknownMap_Is404()
    {
        var response = await RootClient.DeleteAsync($"/api/views/maps/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMap_is_forbidden_for_a_caller_holding_only_ViewTeamMaps_and_leaves_the_row()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamMaps, teamId)
            .SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/views/maps/{map.Id}", Ct);

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
        // A View player.api does not have: its teams route answers 404.

        var response = await RootClient.GetAsync($"/api/views/{viewId}/teams", Ct);

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
    [InlineData("GET", "views/{id}/vms/all")]
    [InlineData("POST", "vms")]
    [InlineData("PUT", "vms/{id}")]
    [InlineData("DELETE", "vms/{id}")]
    [InlineData("POST", "teams/{id}/vms/{id}")]
    [InlineData("DELETE", "teams/{id}/vms/{id}")]
    [InlineData("POST", "views/{id}/map")]
    [InlineData("GET", "views/maps")]
    [InlineData("GET", "views/maps/viewMaps/{id}")]
    [InlineData("GET", "views/{id}/maps/all")]
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

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The system-wide reads are gated on a system permission of their own, not on team visibility.
    [Theory]
    [InlineData("vms")]
    [InlineData("views/maps")]
    public async Task SystemWideReads_are_forbidden_for_a_caller_holding_the_read_only_on_a_team(string route)
    {
        var teamId = Guid.NewGuid();
        await Seed(Vm([teamId]), Map([teamId]));
        var actor = await Actor()
            
                .Holding(AppTeamPermission.ViewTeamVms, teamId)
                .Holding(AppTeamPermission.ViewTeamMaps, teamId)
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/{route}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The all-in-View reads take a system- or View-level permission, and belonging to the View is not
    // a substitute for one.
    [Theory]
    [InlineData("vms/all")]
    [InlineData("maps/all")]
    public async Task AllInViewReads_are_forbidden_for_a_caller_holding_the_read_only_on_a_team_of_the_view(string route)
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        await Seed(Vm([teamId]), Map([teamId], viewId));
        Roster(viewId, teamId);
        var actor = await Actor()
            
                .Holding(AppTeamPermission.ViewTeamVms, teamId)
                .Holding(AppTeamPermission.ViewTeamMaps, teamId)
                .InView(teamId, viewId)
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/{route}", Ct);

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

        Factory.PlayerApiClient.GetMyPermissionsAsync(Arg.Any<CancellationToken>())
            .Returns<ICollection<string>>(_ => throw new ApiException(
                "Team not found", (int)HttpStatusCode.NotFound, null, null, null));

        var response = await RootClient.GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Any other failure from player.api is a 500. Reporting an outage as 403 would send a user to their
    // administrator instead of to whoever can restart player.api.
    [Fact]
    public async Task Get_WhenPlayerApiIsUnreachable_Is500()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);

        Factory.PlayerApiClient.GetMyPermissionsAsync(Arg.Any<CancellationToken>())
            .Returns<ICollection<string>>(_ => throw new ApiException(
                "Service Unavailable", (int)HttpStatusCode.ServiceUnavailable, null, null, null));

        var response = await RootClient.GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    #endregion

    #region Each gate, held exactly

    // Each gate once with exactly the permission it asks for, and once with that permission on another
    // team (or View): the caller is a near miss, so a gate asking about the wrong team lets it through.

    [Fact]
    public async Task GetAll_is_allowed_for_a_caller_holding_exactly_ViewVms()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var actor = await Actor().Holding(AppSystemPermission.ViewVms).SeedAsync();

        var response = await Client(actor).GetAsync("/api/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(vm.Id, (await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Select(x => x.Id));
    }

    [Fact]
    public async Task Get_is_allowed_for_a_caller_holding_exactly_ViewTeamVms_on_the_Vms_team()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ViewTeamVms, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByTeamId_is_allowed_for_a_caller_holding_exactly_ViewTeamVms_on_the_team()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ViewTeamVms, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    // A member of the team, so it sees the team; the Vm permission is on another team.
    [Fact]
    public async Task GetByTeamId_is_forbidden_for_a_member_of_the_team_holding_ViewTeamVms_only_on_another_team()
    {
        var teamId = Guid.NewGuid();
        await Seed(Vm([teamId]));
        var actor = await Actor()
            .MemberOf(teamId)
            .Holding(AppTeamPermission.ViewTeamVms, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/vms", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetByViewId_is_allowed_for_a_caller_holding_exactly_ViewTeamVms_on_a_team_of_the_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().InView(teamId, viewId).Holding(AppTeamPermission.ViewTeamVms, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    // A member of the View's team, so it sees the team; the Vm permission is on a team elsewhere.
    [Fact]
    public async Task GetByViewId_excludes_the_vms_of_a_team_for_a_member_holding_ViewTeamVms_only_on_another_team()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Vm([teamId]));
        var actor = await Actor()
            .InView(teamId, viewId)
            .MemberOf(teamId)
            .Holding(AppTeamPermission.ViewTeamVms, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct));
    }

    // A member of the View's team, so the team is visible; the system Vm permission answers for its Vms.
    [Theory]
    [InlineData(AppSystemPermission.ViewVms)]
    [InlineData(AppSystemPermission.ControlVms)]
    public async Task GetByViewId_is_allowed_for_a_member_holding_exactly_a_system_Vm_read_permission(AppSystemPermission permission)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().InView(teamId, viewId).MemberOf(teamId).Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    // The View permission is carried on the claim of the caller's own team in the View.
    [Theory]
    [InlineData(AppViewPermission.ViewViewVms)]
    [InlineData(AppViewPermission.ControlViewVms)]
    public async Task GetByViewId_is_allowed_for_a_member_holding_exactly_a_Vm_read_permission_on_the_view(AppViewPermission permission)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().InView(teamId, viewId).MemberOf(teamId).Holding(permission, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetByViewId_is_allowed_for_a_caller_holding_exactly_ControlTeamVms_on_a_team_of_the_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().InView(teamId, viewId).Holding(AppTeamPermission.ControlTeamVms, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    // A member of the View's team, holding the system Map permission beside the Vm one it lacks.
    [Fact]
    public async Task GetByViewId_excludes_the_vms_of_a_team_for_a_member_holding_only_the_system_ViewMaps()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Vm([teamId]));
        var actor = await Actor()
            .InView(teamId, viewId)
            .MemberOf(teamId)
            .Holding(AppSystemPermission.ViewMaps)
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct));
    }

    // A member of the View's team, holding the View Map permission beside the Vm one it lacks, on that team.
    [Fact]
    public async Task GetByViewId_excludes_the_vms_of_a_team_for_a_member_holding_only_ViewViewMaps_on_the_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Vm([teamId]));
        var actor = await Actor()
            .InView(teamId, viewId)
            .MemberOf(teamId)
            .Holding(AppViewPermission.ViewViewMaps, viewId)
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct));
    }

    [Fact]
    public async Task GetAllByViewId_is_allowed_for_a_caller_holding_exactly_ViewViewVms_on_the_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([teamId]);
        await Seed(vm);
        Roster(viewId, teamId);
        var actor = await Actor().Holding(AppViewPermission.ViewViewVms, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms/all", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_ViewTeamVms_only_on_another_team()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ViewTeamVms, Guid.NewGuid()).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_is_allowed_for_a_caller_holding_exactly_ManageTeam_on_the_team()
    {
        var teamId = Guid.NewGuid();
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, teamId).SeedAsync();
        var form = CreateForm(teamId, "new-vm");

        var response = await Client(actor).PostAsJsonAsync("/api/vms", form, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.Vms.Where(x => x.Id == form.Id));
    }

    [Fact]
    public async Task Create_is_allowed_for_a_caller_holding_exactly_ManageView_on_the_teams_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var actor = await Actor().Holding(AppViewPermission.ManageView, viewId).SeedAsync();
        var form = CreateForm(teamId, "new-vm");

        var response = await Client(actor).PostAsJsonAsync("/api/vms", form, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.Vms.Where(x => x.Id == form.Id));
    }

    [Fact]
    public async Task Create_is_allowed_for_a_caller_holding_exactly_ManageViews()
    {
        var actor = await Actor().Holding(AppSystemPermission.ManageViews).SeedAsync();
        var form = CreateForm(Guid.NewGuid(), "new-vm");

        var response = await Client(actor).PostAsJsonAsync("/api/vms", form, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.Vms.Where(x => x.Id == form.Id));
    }

    [Fact]
    public async Task Create_is_forbidden_for_a_caller_holding_ManageTeam_only_on_another_team_and_stores_nothing()
    {
        // Gate: src/Player.Vm.Api/Features/Vms/VmService.cs#VmService.CreateAsync
        var (viewId, target, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(target, viewId);
        Factory.Player.PlaceTeam(other, viewId);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, other).SeedAsync();
        var form = CreateForm(target, "new-vm");

        var response = await Client(actor).PostAsJsonAsync("/api/vms", form, Ct);

        Assert.Equal("Insufficient Permissions", (await AssertJsonError(HttpStatusCode.Forbidden, response)).Title);

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == form.Id));
    }

    // Each of the form's teams is checked on its own, so managing the first does not carry the second.
    [Fact]
    public async Task Create_on_two_teams_is_forbidden_for_a_caller_holding_ManageTeam_on_only_the_first_and_stores_nothing()
    {
        // Gate: src/Player.Vm.Api/Features/Vms/VmService.cs#VmService.CreateAsync
        var (viewId, first, second) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(first, viewId);
        Factory.Player.PlaceTeam(second, viewId);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, first).SeedAsync();
        var form = new VmCreateForm { Id = Guid.NewGuid(), Name = "new-vm", TeamIds = [first, second] };

        var response = await Client(actor).PostAsJsonAsync("/api/vms", form, Ct);

        Assert.Equal("Insufficient Permissions", (await AssertJsonError(HttpStatusCode.Forbidden, response)).Title);

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == form.Id));
    }

    [Fact]
    public async Task Update_is_allowed_for_a_caller_holding_exactly_ManageViews()
    {
        var vm = Vm([Guid.NewGuid()], name: "before");
        await Seed(vm);
        var actor = await Actor().Holding(AppSystemPermission.ManageViews).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/vms/{vm.Id}", new VmUpdateForm { Name = "after" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal("after", context.Vms.Single(x => x.Id == vm.Id).Name);
    }

    // Same case as Update_by_a_caller_holding_ManageTeam_on_the_Vms_team_is_403_and_leaves_the_name.
    [Fact]
    public async Task Update_by_a_caller_holding_ManageView_on_the_Vms_view_is_403_and_leaves_the_name()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var vm = Vm([teamId], name: "before");
        await Seed(vm);
        var actor = await Actor().Holding(AppViewPermission.ManageView, viewId).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/vms/{vm.Id}", new VmUpdateForm { Name = "after" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var context = NewContext();
        Assert.Equal("before", context.Vms.Single(x => x.Id == vm.Id).Name);
    }

    /// <summary>A caller holding ManageTeam on the Vm's only team is refused the update.</summary>
    [Fact]
    public async Task Update_by_a_caller_holding_ManageTeam_on_the_Vms_team_is_403_and_leaves_the_name()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId], name: "before");
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, teamId).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/vms/{vm.Id}", new VmUpdateForm { Name = "after" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal("before", context.Vms.Single(x => x.Id == vm.Id).Name);
    }

    [Fact]
    public async Task Update_is_forbidden_for_a_caller_holding_ManageTeam_only_on_another_team_and_leaves_the_name()
    {
        var vm = Vm([Guid.NewGuid()], name: "before");
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, Guid.NewGuid()).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/vms/{vm.Id}", new VmUpdateForm { Name = "after" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal("before", context.Vms.Single(x => x.Id == vm.Id).Name);
    }

    [Fact]
    public async Task Delete_is_allowed_for_a_caller_holding_exactly_ManageTeam_on_the_Vms_team()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, teamId).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == vm.Id));
    }

    [Fact]
    public async Task Delete_is_forbidden_for_a_caller_holding_ManageTeam_only_on_another_team_and_leaves_the_row()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, Guid.NewGuid()).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.Vms.Where(x => x.Id == vm.Id));
    }

    [Fact]
    public async Task AddToTeam_is_allowed_for_a_caller_holding_exactly_ManageTeam_on_the_target_team()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var target = Guid.NewGuid();
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, target).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/teams/{target}/vms/{vm.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.VmTeams.Where(x => x.VmId == vm.Id && x.TeamId == target));
    }

    [Fact]
    public async Task AddToTeam_is_forbidden_for_a_caller_holding_ManageTeam_only_on_the_Vms_current_team()
    {
        var current = Guid.NewGuid();
        var vm = Vm([current]);
        await Seed(vm);
        var target = Guid.NewGuid();
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, current).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/teams/{target}/vms/{vm.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.VmTeams.Where(x => x.VmId == vm.Id && x.TeamId == target));
    }

    [Fact]
    public async Task RemoveFromTeam_is_allowed_for_a_caller_holding_exactly_ManageTeam_on_that_team()
    {
        var (removed, kept) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([removed, kept]);
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, removed).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/teams/{removed}/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal<Guid>([kept], context.VmTeams.Where(x => x.VmId == vm.Id).Select(x => x.TeamId).ToArray());
    }

    [Fact]
    public async Task RemoveFromTeam_is_forbidden_for_a_caller_holding_ManageTeam_only_on_the_other_team()
    {
        var (removed, kept) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([removed, kept]);
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeam, kept).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/teams/{removed}/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal(2, context.VmTeams.Count(x => x.VmId == vm.Id));
    }

    [Fact]
    public async Task GetTeamMap_is_allowed_for_a_caller_holding_exactly_ViewTeamMaps_on_the_team()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        var actor = await Actor().Holding(AppTeamPermission.ViewTeamMaps, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/map", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetTeamMap_is_forbidden_for_a_caller_holding_ViewTeamMaps_only_on_another_team()
    {
        var teamId = Guid.NewGuid();
        await Seed(Map([teamId]));
        var actor = await Actor().Holding(AppTeamPermission.ViewTeamMaps, Guid.NewGuid()).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/map", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ViewTeam as well, because the create looks the team up in player.api as the caller, and
    // player.api's team read asks for it.
    [Fact]
    public async Task CreateMap_is_allowed_for_a_caller_holding_ManageTeamMaps_and_ViewTeam_on_the_team()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        View(viewId, teamId);
        var actor = await Actor()
            .Holding(AppTeamPermission.ManageTeamMaps, teamId)
            .Holding(AppTeamPermission.ViewTeam, teamId)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync($"/api/views/{viewId}/map", MapCreateForm(teamId), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal<Guid>([teamId], context.Maps.Single(x => x.ViewId == viewId).TeamIds);
    }

    /// <summary>A caller holding ManageTeamMaps on the team without ViewTeam is told the team does not exist.</summary>
    [Fact]
    public async Task CreateMap_answers_a_caller_holding_ManageTeamMaps_without_ViewTeam_that_the_team_does_not_exist()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        View(viewId, teamId);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeamMaps, teamId).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync($"/api/views/{viewId}/map", MapCreateForm(teamId), Ct);

        var problem = await AssertJsonError(HttpStatusCode.Forbidden, response);
        Assert.Equal($"Team with id {teamId} does not exist", problem.Title);

        await using var context = NewContext();
        Assert.Empty(context.Maps.Where(x => x.ViewId == viewId));
    }

    // ViewTeam on the Map's team gets the caller past player.api's lookups, so the refusal is the gate's.
    [Fact]
    public async Task CreateMap_is_forbidden_for_a_caller_holding_ManageTeamMaps_only_on_another_team_and_stores_nothing()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        View(viewId, teamId);
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeam, teamId)
            .Holding(AppTeamPermission.ManageTeamMaps, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync($"/api/views/{viewId}/map", MapCreateForm(teamId), Ct);

        var problem = await AssertJsonError(HttpStatusCode.Forbidden, response);
        Assert.Equal("Insufficient Permissions", problem.Title);

        await using var context = NewContext();
        Assert.Empty(context.Maps.Where(x => x.ViewId == viewId));
    }

    // ViewTeam as well, for player.api's team lookup, as for the create.
    [Fact]
    public async Task UpdateMap_is_allowed_for_a_caller_holding_ManageTeamMaps_and_ViewTeam_on_the_team()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        View(map.ViewId, teamId);
        var actor = await Actor()
            .Holding(AppTeamPermission.ManageTeamMaps, teamId)
            .Holding(AppTeamPermission.ViewTeam, teamId)
            .SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/views/maps/{map.Id}", MapUpdateForm(teamId, "after"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal("after", context.Maps.Single(x => x.Id == map.Id).Name);
    }

    // The other team is in the Map's View, the caller may read and manage it, and the form moves the Map
    // onto it: player.api's lookups and the destination check pass, so the refusal is the stored team's.
    [Fact]
    public async Task UpdateMap_is_forbidden_for_a_caller_holding_ManageTeamMaps_only_on_another_team_and_leaves_the_name()
    {
        var (teamId, otherTeam) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId]);
        await Seed(map);
        View(map.ViewId, teamId, otherTeam);
        var actor = await Actor()
            .Holding(AppTeamPermission.ManageTeamMaps, otherTeam)
            .Holding(AppTeamPermission.ViewTeam, otherTeam)
            .SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/views/maps/{map.Id}", MapUpdateForm(otherTeam, "after"), Ct);

        Assert.Equal("Insufficient Permissions", (await AssertJsonError(HttpStatusCode.Forbidden, response)).Title);

        await using var context = NewContext();
        Assert.Equal("map", context.Maps.Single(x => x.Id == map.Id).Name);
    }

    // The caller manages the Map where it is and may read the team it is moved to, so the refusal is the
    // destination's: managing a Map's team is not a licence to put it on any team of the View.
    [Fact]
    public async Task UpdateMap_is_forbidden_for_a_caller_managing_the_maps_team_moving_it_onto_a_team_it_only_views()
    {
        var (teamId, target) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId]);
        await Seed(map);
        View(map.ViewId, teamId, target);
        var actor = await Actor()
            .Holding(AppTeamPermission.ManageTeamMaps, teamId)
            .Holding(AppTeamPermission.ViewTeam, teamId)
            .Holding(AppTeamPermission.ViewTeam, target)
            .SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/views/maps/{map.Id}", MapUpdateForm(target, "after"), Ct);

        Assert.Equal("Insufficient Permissions", (await AssertJsonError(HttpStatusCode.Forbidden, response)).Title);

        await using var context = NewContext();
        Assert.Equal<Guid>([teamId], context.Maps.Single(x => x.Id == map.Id).TeamIds);
    }

    [Fact]
    public async Task DeleteMap_is_allowed_for_a_caller_holding_exactly_ManageTeamMaps_on_the_team()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeamMaps, teamId).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Maps.Where(x => x.Id == map.Id));
    }

    [Fact]
    public async Task DeleteMap_is_allowed_for_a_caller_holding_exactly_the_system_ManageMaps()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(AppSystemPermission.ManageMaps).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Maps.Where(x => x.Id == map.Id));
    }

    // The system ViewMaps reads every Map, and changes none.
    [Fact]
    public async Task DeleteMap_is_forbidden_for_a_caller_holding_only_the_system_ViewMaps_and_leaves_the_row()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(AppSystemPermission.ViewMaps).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.Maps.Where(x => x.Id == map.Id));
    }

    // ViewViews as well, because the create reads the View and the team from player.api as the caller, and
    // player.api's reads ask for it of a caller with no claim in the View. The Map gate does not accept it.
    [Fact]
    public async Task CreateMap_is_allowed_for_a_caller_holding_ManageMaps_and_ViewViews()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        View(viewId, teamId);
        var actor = await Actor()
            .Holding(AppSystemPermission.ManageMaps, AppSystemPermission.ViewViews)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync($"/api/views/{viewId}/map", MapCreateForm(teamId), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal<Guid>([teamId], context.Maps.Single(x => x.ViewId == viewId).TeamIds);
    }

    // The same player.api reads, passed by ViewViews, so the refusal is the Map gate's.
    [Fact]
    public async Task CreateMap_is_forbidden_for_a_caller_holding_ViewMaps_and_ViewViews_and_stores_nothing()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        View(viewId, teamId);
        var actor = await Actor()
            .Holding(AppSystemPermission.ViewMaps, AppSystemPermission.ViewViews)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync($"/api/views/{viewId}/map", MapCreateForm(teamId), Ct);

        Assert.Equal("Insufficient Permissions", (await AssertJsonError(HttpStatusCode.Forbidden, response)).Title);

        await using var context = NewContext();
        Assert.Empty(context.Maps.Where(x => x.ViewId == viewId));
    }

    // ManageViewMaps is carried on the caller's own team in the Map's View, which covers every team there.
    [Fact]
    public async Task DeleteMap_is_allowed_for_a_caller_holding_exactly_ManageViewMaps_on_the_maps_view()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(AppViewPermission.ManageViewMaps, map.ViewId).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Maps.Where(x => x.Id == map.Id));
    }

    // A member of the Map's team, holding the View permission the delete accepts in another View.
    [Fact]
    public async Task DeleteMap_is_forbidden_for_a_member_of_the_maps_team_holding_ManageViewMaps_only_in_another_view_and_leaves_the_row()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        var actor = await Actor()
            .MemberOf(teamId)
            .Holding(AppViewPermission.ManageViewMaps, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.Maps.Where(x => x.Id == map.Id));
    }

    // ViewTeam on the new team as well, for player.api's team lookup; the Map gate does not accept it, and
    // ManageViewMaps in the View covers both the team the Map is on and the one it moves to.
    [Fact]
    public async Task UpdateMap_moving_a_map_between_teams_is_allowed_for_a_caller_holding_ManageViewMaps_on_the_maps_view_and_ViewTeam_on_the_new_team()
    {
        var (teamId, otherTeam) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId]);
        await Seed(map);
        View(map.ViewId, teamId, otherTeam);
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeam, otherTeam)
            .Holding(AppViewPermission.ManageViewMaps, map.ViewId)
            .SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/views/maps/{map.Id}", MapUpdateForm(otherTeam, "after"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal<Guid>([otherTeam], context.Maps.Single(x => x.Id == map.Id).TeamIds);
    }

    // The caller may read and manage the team the Map moves to, so the destination check passes and the
    // refusal is the check on the team the Map is on, where ManageViewMaps held in another View does not reach.
    [Fact]
    public async Task UpdateMap_off_its_team_is_forbidden_for_a_caller_holding_ManageViewMaps_only_in_another_view_and_leaves_the_teams()
    {
        var (teamId, otherTeam) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId]);
        await Seed(map);
        View(map.ViewId, teamId, otherTeam);
        var actor = await Actor()
            .Holding(AppTeamPermission.ManageTeamMaps, otherTeam)
            .Holding(AppTeamPermission.ViewTeam, otherTeam)
            .Holding(AppViewPermission.ManageViewMaps, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/views/maps/{map.Id}", MapUpdateForm(otherTeam, "after"), Ct);

        Assert.Equal("Insufficient Permissions", (await AssertJsonError(HttpStatusCode.Forbidden, response)).Title);

        await using var context = NewContext();
        Assert.Equal<Guid>([teamId], context.Maps.Single(x => x.Id == map.Id).TeamIds);
    }

    #endregion

    #region Every value and tier the Vm and map gates read

    // Each permission value a gate accepts, held alone by a caller who is not Root, and for each tier of a
    // gate a near miss of its own: a neighbouring system permission, or the same View permission in
    // another View player.api knows. The Vm reads and the map reads are neighbours of each other.

    /// <summary>The title <c>ForbiddenException()</c> carries, which every VmService gate without a message of its own answers with.</summary>
    private const string Refused = "Insufficient Permissions";

    /// <summary>The title of the personal-Vm refusal in <c>CanAccessVm</c>.</summary>
    private const string PersonalRefused = "This machine belongs to another user";

    /// <summary>The title <c>GetMapAsync</c> refuses with.</summary>
    private const string MapRefused = "You do not have access to this map";

    [Fact]
    public async Task GetAll_is_allowed_for_a_caller_holding_exactly_ControlVms()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var actor = await Actor().Holding(AppSystemPermission.ControlVms).SeedAsync();

        var response = await Client(actor).GetAsync("/api/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(vm.Id, (await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Select(x => x.Id));
    }

    [Fact]
    public async Task GetAll_is_forbidden_for_a_caller_holding_only_the_system_ViewMaps()
    {
        await Seed(Vm([Guid.NewGuid()]));
        var actor = await Actor().Holding(AppSystemPermission.ViewMaps).SeedAsync();

        var response = await Client(actor).GetAsync("/api/vms", Ct);

        await AssertRefused(response, Refused);
    }

    [Theory]
    [InlineData(AppSystemPermission.ViewVms)]
    [InlineData(AppSystemPermission.ControlVms)]
    public async Task Get_is_allowed_for_a_caller_holding_exactly_a_system_Vm_read_permission(AppSystemPermission permission)
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var actor = await Actor().Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content.ReadFromJsonAsync<VmModel>(JsonOptions, Ct)).Id);
    }

    [Theory]
    [InlineData(AppViewPermission.ViewViewVms)]
    [InlineData(AppViewPermission.ControlViewVms)]
    public async Task Get_is_allowed_for_a_caller_holding_exactly_a_Vm_read_permission_on_the_Vms_view(AppViewPermission permission)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().Holding(permission, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content.ReadFromJsonAsync<VmModel>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task Get_is_allowed_for_a_caller_holding_exactly_ControlTeamVms_on_the_Vms_team()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ControlTeamVms, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content.ReadFromJsonAsync<VmModel>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_only_the_system_ViewMaps()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var actor = await Actor().Holding(AppSystemPermission.ViewMaps).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        await AssertRefused(response, Refused);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_ViewViewVms_only_in_another_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().Holding(AppViewPermission.ViewViewVms, AnotherKnownView()).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        await AssertRefused(response, Refused);
    }

    [Theory]
    [InlineData(AppSystemPermission.ViewVms)]
    [InlineData(AppSystemPermission.ControlVms)]
    public async Task Get_of_another_users_personal_vm_is_allowed_for_a_caller_holding_exactly_a_system_Vm_read_permission(AppSystemPermission permission)
    {
        var vm = Vm([Guid.NewGuid()], userId: Guid.NewGuid());
        await Seed(vm);
        var actor = await Actor().Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content.ReadFromJsonAsync<VmModel>(JsonOptions, Ct)).Id);
    }

    [Theory]
    [InlineData(AppViewPermission.ViewViewVms)]
    [InlineData(AppViewPermission.ControlViewVms)]
    public async Task Get_of_another_users_personal_vm_is_allowed_for_a_caller_holding_exactly_a_Vm_read_permission_on_the_Vms_view(AppViewPermission permission)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var vm = Vm([teamId], userId: Guid.NewGuid());
        await Seed(vm);
        var actor = await Actor().Holding(permission, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content.ReadFromJsonAsync<VmModel>(JsonOptions, Ct)).Id);
    }

    // ViewTeamVms on the Vm's team passes the team check, so the personal-Vm check is the one that refuses.
    [Fact]
    public async Task Get_of_another_users_personal_vm_is_forbidden_for_a_caller_holding_ViewTeamVms_and_only_the_system_ViewMaps()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId], userId: Guid.NewGuid());
        await Seed(vm);
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamVms, teamId)
            .Holding(AppSystemPermission.ViewMaps)
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        await AssertRefused(response, PersonalRefused);
    }

    [Fact]
    public async Task Get_of_another_users_personal_vm_is_forbidden_for_a_caller_holding_ViewTeamVms_and_ViewViewVms_only_in_another_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var vm = Vm([teamId], userId: Guid.NewGuid());
        await Seed(vm);
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamVms, teamId)
            .Holding(AppViewPermission.ViewViewVms, AnotherKnownView())
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/vms/{vm.Id}", Ct);

        await AssertRefused(response, PersonalRefused);
    }

    // A member of the team, so it sees the team, and the Vm permission is what the gate reads.
    [Theory]
    [InlineData(AppSystemPermission.ViewVms)]
    [InlineData(AppSystemPermission.ControlVms)]
    public async Task GetByTeamId_is_allowed_for_a_member_holding_exactly_a_system_Vm_read_permission(AppSystemPermission permission)
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().MemberOf(teamId).Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    [Theory]
    [InlineData(AppViewPermission.ViewViewVms)]
    [InlineData(AppViewPermission.ControlViewVms)]
    public async Task GetByTeamId_is_allowed_for_a_member_holding_exactly_a_Vm_read_permission_on_the_teams_view(AppViewPermission permission)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().InView(teamId, viewId).MemberOf(teamId).Holding(permission, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetByTeamId_is_allowed_for_a_caller_holding_exactly_ControlTeamVms_on_the_team()
    {
        var teamId = Guid.NewGuid();
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().Holding(AppTeamPermission.ControlTeamVms, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/vms", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetByTeamId_is_forbidden_for_a_member_of_the_team_holding_only_the_system_ViewMaps()
    {
        var teamId = Guid.NewGuid();
        await Seed(Vm([teamId]));
        var actor = await Actor().MemberOf(teamId).Holding(AppSystemPermission.ViewMaps).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/vms", Ct);

        await AssertRefused(response, Refused);
    }

    [Fact]
    public async Task GetByTeamId_is_forbidden_for_a_member_of_the_team_holding_ViewViewVms_only_in_another_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Vm([teamId]));
        var actor = await Actor()
            .InView(teamId, viewId)
            .MemberOf(teamId)
            .Holding(AppViewPermission.ViewViewVms, AnotherKnownView())
            .SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/vms", Ct);

        await AssertRefused(response, Refused);
    }

    [Theory]
    [InlineData(AppSystemPermission.ViewVms)]
    [InlineData(AppSystemPermission.ControlVms)]
    public async Task GetAllByViewId_is_allowed_for_a_caller_holding_exactly_a_system_Vm_read_permission(AppSystemPermission permission)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([teamId]);
        await Seed(vm);
        Roster(viewId, teamId);
        var actor = await Actor().Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms/all", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetAllByViewId_is_allowed_for_a_caller_holding_exactly_ControlViewVms_on_the_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([teamId]);
        await Seed(vm);
        Roster(viewId, teamId);
        var actor = await Actor().Holding(AppViewPermission.ControlViewVms, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms/all", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetAllByViewId_is_forbidden_for_a_caller_holding_only_the_system_ViewMaps()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Vm([teamId]));
        Roster(viewId, teamId);
        Factory.Player.AddView(viewId);
        var actor = await Actor().Holding(AppSystemPermission.ViewMaps).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms/all", Ct);

        await AssertRefused(response, Refused);
    }

    [Fact]
    public async Task GetAllByViewId_is_forbidden_for_a_caller_holding_ViewViewVms_only_in_another_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Vm([teamId]));
        Roster(viewId, teamId);
        Factory.Player.AddView(viewId);
        var actor = await Actor().Holding(AppViewPermission.ViewViewVms, AnotherKnownView()).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/vms/all", Ct);

        await AssertRefused(response, Refused);
    }

    [Fact]
    public async Task GetAllMaps_is_allowed_for_a_caller_holding_exactly_ManageMaps()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(AppSystemPermission.ManageMaps).SeedAsync();

        var response = await Client(actor).GetAsync("/api/views/maps", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Select(x => x.Id));
    }

    [Fact]
    public async Task GetAllMaps_is_forbidden_for_a_caller_holding_only_the_system_ViewVms()
    {
        await Seed(Map([Guid.NewGuid()]));
        var actor = await Actor().Holding(AppSystemPermission.ViewVms).SeedAsync();

        var response = await Client(actor).GetAsync("/api/views/maps", Ct);

        await AssertRefused(response, Refused);
    }

    // Only a teamless map, so the View-level check decides alone.
    [Theory]
    [InlineData(AppSystemPermission.ViewMaps)]
    [InlineData(AppSystemPermission.ManageMaps)]
    public async Task GetViewMaps_includes_the_teamless_maps_for_a_caller_holding_exactly_a_system_Map_read_permission(AppSystemPermission permission)
    {
        var viewId = Guid.NewGuid();
        var teamless = Map([], viewId);
        await Seed(teamless);
        var actor = await Actor().Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(teamless.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetViewMaps_includes_the_teamless_maps_for_a_caller_holding_exactly_ManageViewMaps_on_the_view()
    {
        var viewId = Guid.NewGuid();
        var teamless = Map([], viewId);
        await Seed(teamless);
        var actor = await Actor().Holding(AppViewPermission.ManageViewMaps, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(teamless.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetViewMaps_leaves_out_the_teamless_maps_for_a_caller_holding_only_the_system_ViewVms()
    {
        var viewId = Guid.NewGuid();
        await Seed(Map([], viewId));
        var actor = await Actor().Holding(AppSystemPermission.ViewVms).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/viewMaps/{viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct));
    }

    [Theory]
    [InlineData(AppSystemPermission.ViewMaps)]
    [InlineData(AppSystemPermission.ManageMaps)]
    public async Task GetAllViewMaps_is_allowed_for_a_caller_holding_exactly_a_system_Map_read_permission(AppSystemPermission permission)
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId], viewId);
        await Seed(map);
        Roster(viewId, teamId);
        var actor = await Actor().Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/maps/all", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetAllViewMaps_is_allowed_for_a_caller_holding_exactly_ManageViewMaps_on_the_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        var map = Map([teamId], viewId);
        await Seed(map);
        Roster(viewId, teamId);
        var actor = await Actor().Holding(AppViewPermission.ManageViewMaps, viewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/maps/all", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, Assert.Single(await response.Content.ReadFromJsonAsync<VmMapModel[]>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetAllViewMaps_is_forbidden_for_a_caller_holding_only_the_system_ViewVms()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Map([teamId], viewId));
        Roster(viewId, teamId);
        var actor = await Actor().Holding(AppSystemPermission.ViewVms).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/maps/all", Ct);

        await AssertRefused(response, Refused);
    }

    [Fact]
    public async Task GetAllViewMaps_is_forbidden_for_a_caller_holding_ViewViewMaps_only_in_another_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(Map([teamId], viewId));
        Roster(viewId, teamId);
        var actor = await Actor().Holding(AppViewPermission.ViewViewMaps, AnotherKnownView()).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/{viewId}/maps/all", Ct);

        await AssertRefused(response, Refused);
    }

    [Theory]
    [InlineData(AppSystemPermission.ViewMaps)]
    [InlineData(AppSystemPermission.ManageMaps)]
    public async Task GetMap_is_allowed_for_a_caller_holding_exactly_a_system_Map_read_permission(AppSystemPermission permission)
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel>(JsonOptions, Ct)).Id);
    }

    [Theory]
    [InlineData(AppViewPermission.ViewViewMaps)]
    [InlineData(AppViewPermission.ManageViewMaps)]
    public async Task GetMap_is_allowed_for_a_caller_holding_exactly_a_Map_read_permission_on_the_maps_view(AppViewPermission permission)
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(permission, map.ViewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetMap_is_allowed_for_a_caller_holding_exactly_ManageTeamMaps_on_the_maps_team()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeamMaps, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/{map.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetMap_is_forbidden_for_a_caller_holding_only_the_system_ViewVms()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(AppSystemPermission.ViewVms).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/{map.Id}", Ct);

        await AssertRefused(response, MapRefused);
    }

    [Fact]
    public async Task GetMap_is_forbidden_for_a_caller_holding_ViewViewMaps_only_in_another_view()
    {
        var map = Map([Guid.NewGuid()]);
        await Seed(map);
        var actor = await Actor().Holding(AppViewPermission.ViewViewMaps, AnotherKnownView()).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/views/maps/{map.Id}", Ct);

        await AssertRefused(response, MapRefused);
    }

    [Theory]
    [InlineData(AppSystemPermission.ViewMaps)]
    [InlineData(AppSystemPermission.ManageMaps)]
    public async Task GetTeamMap_is_allowed_for_a_caller_holding_exactly_a_system_Map_read_permission(AppSystemPermission permission)
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        var actor = await Actor().Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/map", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel>(JsonOptions, Ct)).Id);
    }

    [Theory]
    [InlineData(AppViewPermission.ViewViewMaps)]
    [InlineData(AppViewPermission.ManageViewMaps)]
    public async Task GetTeamMap_is_allowed_for_a_caller_holding_exactly_a_Map_read_permission_on_the_teams_view(AppViewPermission permission)
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        var actor = await Actor().Holding(permission, map.ViewId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/map", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetTeamMap_is_allowed_for_a_caller_holding_exactly_ManageTeamMaps_on_the_team()
    {
        var teamId = Guid.NewGuid();
        var map = Map([teamId]);
        await Seed(map);
        var actor = await Actor().Holding(AppTeamPermission.ManageTeamMaps, teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/map", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(map.Id, (await response.Content.ReadFromJsonAsync<VmMapModel>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task GetTeamMap_is_forbidden_for_a_caller_holding_only_the_system_ViewVms()
    {
        var teamId = Guid.NewGuid();
        await Seed(Map([teamId]));
        var actor = await Actor().Holding(AppSystemPermission.ViewVms).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/map", Ct);

        await AssertRefused(response, Refused);
    }

    [Fact]
    public async Task GetTeamMap_is_forbidden_for_a_caller_holding_ViewViewMaps_only_in_another_view()
    {
        var teamId = Guid.NewGuid();
        await Seed(Map([teamId]));
        var actor = await Actor().Holding(AppViewPermission.ViewViewMaps, AnotherKnownView()).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/teams/{teamId}/map", Ct);

        await AssertRefused(response, Refused);
    }

    [Fact]
    public async Task Create_is_forbidden_for_a_caller_holding_only_the_system_ViewViews_and_stores_nothing()
    {
        var actor = await Actor().Holding(AppSystemPermission.ViewViews).SeedAsync();
        var form = CreateForm(Guid.NewGuid(), "new-vm");

        var response = await Client(actor).PostAsJsonAsync("/api/vms", form, Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == form.Id));
    }

    [Fact]
    public async Task Create_is_forbidden_for_a_caller_holding_ManageView_only_in_another_view_and_stores_nothing()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var actor = await Actor().Holding(AppViewPermission.ManageView, AnotherKnownView()).SeedAsync();
        var form = CreateForm(teamId, "new-vm");

        var response = await Client(actor).PostAsJsonAsync("/api/vms", form, Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == form.Id));
    }

    [Fact]
    public async Task Update_is_forbidden_for_a_caller_holding_only_the_system_ViewViews_and_leaves_the_name()
    {
        var vm = Vm([Guid.NewGuid()], name: "before");
        await Seed(vm);
        var actor = await Actor().Holding(AppSystemPermission.ViewViews).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/vms/{vm.Id}", new VmUpdateForm { Name = "after" }, Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Equal("before", context.Vms.Single(x => x.Id == vm.Id).Name);
    }

    [Fact]
    public async Task Update_is_forbidden_for_a_caller_holding_ManageView_only_in_another_view_and_leaves_the_name()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var vm = Vm([teamId], name: "before");
        await Seed(vm);
        var actor = await Actor().Holding(AppViewPermission.ManageView, AnotherKnownView()).SeedAsync();

        var response = await Client(actor).PutAsJsonAsync($"/api/vms/{vm.Id}", new VmUpdateForm { Name = "after" }, Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Equal("before", context.Vms.Single(x => x.Id == vm.Id).Name);
    }

    [Fact]
    public async Task Delete_is_allowed_for_a_caller_holding_exactly_ManageViews()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var actor = await Actor().Holding(AppSystemPermission.ManageViews).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == vm.Id));
    }

    [Fact]
    public async Task Delete_is_allowed_for_a_caller_holding_exactly_ManageView_on_the_Vms_view()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().Holding(AppViewPermission.ManageView, viewId).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.Vms.Where(x => x.Id == vm.Id));
    }

    [Fact]
    public async Task Delete_is_forbidden_for_a_caller_holding_only_the_system_ViewViews_and_leaves_the_row()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var actor = await Actor().Holding(AppSystemPermission.ViewViews).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/vms/{vm.Id}", Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Single(context.Vms.Where(x => x.Id == vm.Id));
    }

    [Fact]
    public async Task Delete_is_forbidden_for_a_caller_holding_ManageView_only_in_another_view_and_leaves_the_row()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(teamId, viewId);
        var vm = Vm([teamId]);
        await Seed(vm);
        var actor = await Actor().Holding(AppViewPermission.ManageView, AnotherKnownView()).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/vms/{vm.Id}", Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Single(context.Vms.Where(x => x.Id == vm.Id));
    }

    [Fact]
    public async Task AddToTeam_is_allowed_for_a_caller_holding_exactly_ManageViews()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var target = Guid.NewGuid();
        var actor = await Actor().Holding(AppSystemPermission.ManageViews).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/teams/{target}/vms/{vm.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.VmTeams.Where(x => x.VmId == vm.Id && x.TeamId == target));
    }

    [Fact]
    public async Task AddToTeam_is_allowed_for_a_caller_holding_exactly_ManageView_on_the_target_teams_view()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var (viewId, target) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(target, viewId);
        var actor = await Actor().Holding(AppViewPermission.ManageView, viewId).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/teams/{target}/vms/{vm.Id}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewContext();
        Assert.Single(context.VmTeams.Where(x => x.VmId == vm.Id && x.TeamId == target));
    }

    [Fact]
    public async Task AddToTeam_is_forbidden_for_a_caller_holding_only_the_system_ViewViews()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var target = Guid.NewGuid();
        var actor = await Actor().Holding(AppSystemPermission.ViewViews).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/teams/{target}/vms/{vm.Id}", null, Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Empty(context.VmTeams.Where(x => x.VmId == vm.Id && x.TeamId == target));
    }

    [Fact]
    public async Task AddToTeam_is_forbidden_for_a_caller_holding_ManageView_only_in_another_view()
    {
        var vm = Vm([Guid.NewGuid()]);
        await Seed(vm);
        var (viewId, target) = (Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(target, viewId);
        var actor = await Actor().Holding(AppViewPermission.ManageView, AnotherKnownView()).SeedAsync();

        var response = await Client(actor).PostAsync($"/api/teams/{target}/vms/{vm.Id}", null, Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Empty(context.VmTeams.Where(x => x.VmId == vm.Id && x.TeamId == target));
    }

    [Fact]
    public async Task RemoveFromTeam_is_allowed_for_a_caller_holding_exactly_ManageViews()
    {
        var (removed, kept) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([removed, kept]);
        await Seed(vm);
        var actor = await Actor().Holding(AppSystemPermission.ManageViews).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/teams/{removed}/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal<Guid>([kept], context.VmTeams.Where(x => x.VmId == vm.Id).Select(x => x.TeamId).ToArray());
    }

    [Fact]
    public async Task RemoveFromTeam_is_allowed_for_a_caller_holding_exactly_ManageView_on_that_teams_view()
    {
        var (viewId, removed, kept) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(removed, viewId);
        var vm = Vm([removed, kept]);
        await Seed(vm);
        var actor = await Actor().Holding(AppViewPermission.ManageView, viewId).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/teams/{removed}/vms/{vm.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal<Guid>([kept], context.VmTeams.Where(x => x.VmId == vm.Id).Select(x => x.TeamId).ToArray());
    }

    [Fact]
    public async Task RemoveFromTeam_is_forbidden_for_a_caller_holding_only_the_system_ViewViews()
    {
        var (removed, kept) = (Guid.NewGuid(), Guid.NewGuid());
        var vm = Vm([removed, kept]);
        await Seed(vm);
        var actor = await Actor().Holding(AppSystemPermission.ViewViews).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/teams/{removed}/vms/{vm.Id}", Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Equal(2, context.VmTeams.Count(x => x.VmId == vm.Id));
    }

    [Fact]
    public async Task RemoveFromTeam_is_forbidden_for_a_caller_holding_ManageView_only_in_another_view()
    {
        var (viewId, removed, kept) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Factory.Player.PlaceTeam(removed, viewId);
        var vm = Vm([removed, kept]);
        await Seed(vm);
        var actor = await Actor().Holding(AppViewPermission.ManageView, AnotherKnownView()).SeedAsync();

        var response = await Client(actor).DeleteAsync($"/api/teams/{removed}/vms/{vm.Id}", Ct);

        await AssertRefused(response, Refused);

        await using var context = NewContext();
        Assert.Equal(2, context.VmTeams.Count(x => x.VmId == vm.Id));
    }

    #endregion

    #region Helpers

    /// <summary>A View player.api knows, with nothing of this test's in it: the "another View" of a near miss.</summary>
    private Guid AnotherKnownView()
    {
        var viewId = Guid.NewGuid();
        Factory.Player.AddView(viewId);
        return viewId;
    }

    /// <summary>A 403 whose title is the one the gate under test refuses with.</summary>
    private async Task AssertRefused(HttpResponseMessage response, string title) =>
        Assert.Equal(title, (await AssertJsonError(HttpStatusCode.Forbidden, response)).Title);

    private static IEnumerable<Guid> Sorted(params Guid[] ids) => ids.OrderBy(x => x);

    private async Task<T> Get<T>(string route)
    {
        var response = await RootClient.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    private void Roster(Guid viewId, params Guid[] teamIds) =>
        Factory.Views.GetTeamsForView(viewId, true, Arg.Any<CancellationToken>()).Returns(teamIds.ToList());

    /// <summary>
    /// Makes <paramref name="teamId"/> Root's primary team in its View (a View of its own unless placed),
    /// so Root sees it.
    /// </summary>
    private void TeamVisibility(Guid teamId)
    {
        var viewId = Factory.Player.ViewOf(teamId) ?? Guid.NewGuid();
        Factory.Player.Sees(Root.Id, viewId, teamId, false, [teamId]);
    }

    /// <summary>
    /// The View in player.api with these teams placed in it, and Root's visibility there: the first team
    /// is Root's primary team and the rest are scoped onto it.
    /// </summary>
    private void View(Guid viewId, params Guid[] teamIds)
    {
        Factory.Player.AddView(viewId);
        Factory.Player.Sees(Root.Id, viewId, teamIds.Length == 0 ? null : teamIds[0], false, teamIds);
    }

    private static VmCreateForm CreateForm(Guid teamId, string name) =>
        new() { Id = Guid.NewGuid(), Name = name, TeamIds = [teamId] };

    private static VmMapCreateForm MapCreateForm(Guid teamId) =>
        new() { Name = "floor-1", ImageUrl = "https://images/floor-1.png", TeamIds = [teamId], Coordinates = [] };

    private static VmMapUpdateForm MapUpdateForm(Guid teamId, string name) =>
        new() { Name = name, ImageUrl = "https://images/map.png", TeamIds = [teamId], Coordinates = [] };

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

    /// <summary>A Map, with its View and teams placed in player.api: a Map's teams are teams of its View.</summary>
    private VmMapEntity Map(Guid[] teamIds, Guid? viewId = null)
    {
        var map = new VmMapEntity
        {
            Name = "map",
            ImageUrl = "https://images/map.png",
            ViewId = viewId ?? Guid.NewGuid(),
            TeamIds = [.. teamIds]
        };

        Factory.Player.AddView(map.ViewId);
        foreach (var teamId in teamIds)
        {
            if (Factory.Player.ViewOf(teamId) != map.ViewId)
                Factory.Player.PlaceTeam(teamId, map.ViewId);
        }

        return map;
    }

    #endregion
}
