// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Options;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The service every other part of this API asks "what view is this team in?", over a substituted
/// transport. Almost every test in the suite substitutes <c>IViewService</c> outright, so what is left is
/// the thing they substitute: two player.api calls, a memory cache in front of them, and one status code
/// singled out for forgiveness.
/// </summary>
/// <remarks>
/// <para>
/// The cache is the reason this is worth testing at all. It is the application's singleton
/// <c>IMemoryCache</c>, keyed by bare team and view guids with a fifteen-minute sliding expiration and no
/// invalidation anywhere - not on a webhook, not on a SignalR event. So every answer here is a promise
/// held for fifteen minutes after the last time it was asked for, across every request in the process,
/// and a team that moves between views is invisible until it lapses.
/// </para>
/// <para>
/// The client is the generated <c>PlayerApiClient</c> from Player.Api.Client, built over an
/// <see cref="HttpClient"/> the way <c>ViewService</c>'s constructor builds it, so the routes and the
/// deserialization in these tests are that package's rather than this repository's - see
/// <see cref="TestHttpHandler"/>.
/// </para>
/// </remarks>
public class ViewServiceTests
{
    private readonly TestHttpHandler _http = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IViewService _service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ViewServiceTests()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_http, disposeHandler: false));

        _service = new ViewService(
            factory,
            _cache,
            new ClientOptions { urls = new ApiUrlSettings { playerApi = "https://player.test.local/" } },
            Substitute.For<ILogger<ViewService>>());
    }

    #region The team's view

    /// <summary>
    /// The ordinary answer, and the two requests it takes: the team names its view, and the view is then
    /// fetched for its name. Nothing here needs the view's name - <c>GetViewIdForTeam</c> returns a guid -
    /// but the cached entry is shared with <see cref="GetInfoForTeams_ReturnsTheViewName"/>'s caller, so
    /// both requests happen either way.
    /// </summary>
    [Fact]
    public async Task GetViewIdForTeam_ReturnsTheViewTheTeamIsIn()
    {
        var (teamId, viewId) = (Guid.NewGuid(), Guid.NewGuid());
        Team(teamId, viewId);
        View(viewId, "Exercise");

        Assert.Equal(viewId, await _service.GetViewIdForTeam(teamId, Ct));

        Assert.Equal<string>([$"api/teams/{teamId}", $"api/views/{viewId}"], _http.Paths);
    }

    /// <summary>
    /// A team player.api does not have is not an error here: 404 is caught and logged, and the caller is
    /// told the team is in no view. This is what makes a Vm on a team of a deleted view harmless to every
    /// caller that only wants to know which SignalR groups to send to.
    /// </summary>
    [Fact]
    public async Task GetViewIdForTeam_ForATeamPlayerDoesNotHave_ReturnsNull()
    {
        var teamId = Guid.NewGuid();
        _http.Answers($"api/teams/{teamId}", HttpStatusCode.NotFound);

        Assert.Null(await _service.GetViewIdForTeam(teamId, Ct));
    }

    /// <summary>
    /// Only 404 is forgiven. player.api being broken or unreachable propagates, which is the right way
    /// round: a 500 answered as "this team is in no view" would silently stop announcements for every team
    /// for the fifteen minutes the answer is then cached.
    /// </summary>
    [Fact]
    public async Task GetViewIdForTeam_WhenPlayerFails_Throws()
    {
        var teamId = Guid.NewGuid();
        _http.Answers($"api/teams/{teamId}", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<ApiException>(() => _service.GetViewIdForTeam(teamId, Ct));

        Assert.Equal(500, ex.StatusCode);
    }

    /// <summary>
    /// The 404 is caught inside the call the cache wraps, so "no view" is cached like any other answer. A
    /// team that appears in player.api a moment later - the ordinary race, since a Vm can be added to a
    /// team of a view still being created - keeps reporting no view until the entry lapses.
    /// </summary>
    /// <remarks>
    /// Characterized, not endorsed. The consequence is a Vm that stays out of its view's SignalR group for
    /// up to fifteen minutes; the sliding expiration means every failed lookup pushes that further out. The
    /// arrangement proves it by answering 404 once and the real team afterwards: a second request would
    /// have found the team, and none is made.
    /// </remarks>
    [Fact]
    public async Task GetViewIdForTeam_CachesTheAbsenceOfATeam()
    {
        var (teamId, viewId) = (Guid.NewGuid(), Guid.NewGuid());
        _http.AnswersOnce($"api/teams/{teamId}", HttpStatusCode.NotFound);
        Team(teamId, viewId);
        View(viewId, "Exercise");

        Assert.Null(await _service.GetViewIdForTeam(teamId, Ct));
        Assert.Null(await _service.GetViewIdForTeam(teamId, Ct));

        Assert.Equal<string>([$"api/teams/{teamId}"], _http.Paths);
    }

    /// <summary>
    /// The cache in the ordinary direction: a team asked about twice is fetched once. Worth pinning because
    /// this is on the path of every announcement <c>VmHub</c> and the entity-event handlers make, several
    /// per Vm per save, and without it each would be two calls out to player.api.
    /// </summary>
    [Fact]
    public async Task GetViewIdForTeam_AsksPlayerOnceForATeamItHasSeen()
    {
        var (teamId, viewId) = (Guid.NewGuid(), Guid.NewGuid());
        Team(teamId, viewId);
        View(viewId, "Exercise");

        await _service.GetViewIdForTeam(teamId, Ct);
        await _service.GetViewIdForTeam(teamId, Ct);

        Assert.Equal(2, _http.Sent.Count);
    }

    #endregion

    #region Several teams at once

    /// <summary>
    /// Teams in the same view collapse to one view id, which is what the callers want: the view group is
    /// told once however many of its teams a Vm is on.
    /// </summary>
    [Fact]
    public async Task GetViewIdsForTeams_ReturnsEachViewOnce()
    {
        var (first, second, viewId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Team(first, viewId);
        Team(second, viewId);
        View(viewId, "Exercise");

        var viewIds = await _service.GetViewIdsForTeams([first, second], Ct);

        Assert.Equal<Guid>([viewId], viewIds);
    }

    [Fact]
    public async Task GetViewIdsForTeams_ReturnsAViewForEachDistinctOne()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var (firstView, secondView) = (Guid.NewGuid(), Guid.NewGuid());
        Team(first, firstView);
        Team(second, secondView);
        View(firstView, "First");
        View(secondView, "Second");

        var viewIds = await _service.GetViewIdsForTeams([first, second], Ct);

        Assert.Equal<Guid>([firstView, secondView], viewIds);
    }

    /// <summary>
    /// A team in no view contributes nothing, so a Vm on one team of a deleted view and one live team is
    /// still announced to the live team's view.
    /// </summary>
    [Fact]
    public async Task GetViewIdsForTeams_SkipsATeamPlayerDoesNotHave()
    {
        var (missing, live, viewId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _http.Answers($"api/teams/{missing}", HttpStatusCode.NotFound);
        Team(live, viewId);
        View(viewId, "Exercise");

        var viewIds = await _service.GetViewIdsForTeams([missing, live], Ct);

        Assert.Equal<Guid>([viewId], viewIds);
    }

    /// <summary>
    /// The view's name, which is the one thing <c>TeamInfo</c> carries that no other method exposes. Its
    /// only caller is <c>ActiveVirtualMachineService</c>'s telemetry, which tags a metric with it.
    /// </summary>
    [Fact]
    public async Task GetInfoForTeams_ReturnsTheViewName()
    {
        var (teamId, viewId) = (Guid.NewGuid(), Guid.NewGuid());
        Team(teamId, viewId, name: "Red Team");
        View(viewId, "Exercise");

        var info = Assert.Single(await _service.GetInfoForTeams([teamId], Ct));

        Assert.Equal(viewId, info.ViewId);
        Assert.Equal("Exercise", info.ViewName);
        Assert.Equal("Red Team", info.TeamName);
    }

    /// <summary>
    /// Two teams of one view produce one entry, not two - the list is deduplicated by view id, so the team
    /// name that survives is whichever team was passed first. Callers asking for information about teams
    /// get information about views.
    /// </summary>
    /// <remarks>
    /// Characterized. It suits the only caller, which records one metric per view, but the method's name
    /// and its <c>TeamName</c> field promise something else, and a second caller wanting a name per team
    /// would silently lose entries.
    /// </remarks>
    [Fact]
    public async Task GetInfoForTeams_ForTwoTeamsOfOneView_ReturnsOneEntryNamedForTheFirst()
    {
        var (first, second, viewId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Team(first, viewId, name: "Red Team");
        Team(second, viewId, name: "Blue Team");
        View(viewId, "Exercise");

        var info = await _service.GetInfoForTeams([first, second], Ct);

        Assert.Equal("Red Team", Assert.Single(info).TeamName);
    }

    /// <summary>
    /// A team player.api does not have still produces an entry - an empty <c>TeamInfo</c>, with a null view
    /// id - because the deduplication is by view id and no other entry has claimed null yet.
    /// </summary>
    /// <remarks>
    /// Characterized, and the sharp edge of the previous test: <c>GetInfoForTeams</c>'s only caller,
    /// <c>ActiveVirtualMachineService.SetViewActiveConsolesTelemetry</c>, casts <c>teamInfo.ViewId</c> to
    /// <see cref="Guid"/> unchecked, so this entry throws <see cref="InvalidOperationException"/> there.
    /// Reaching it needs a console opened from a team player.api has forgotten, which is why nothing has
    /// noticed. <see cref="GetViewIdsForTeams_SkipsATeamPlayerDoesNotHave"/> is the same input through the
    /// method that does check.
    /// </remarks>
    [Fact]
    public async Task GetInfoForTeams_ForATeamPlayerDoesNotHave_ReturnsAnEmptyEntry()
    {
        var teamId = Guid.NewGuid();
        _http.Answers($"api/teams/{teamId}", HttpStatusCode.NotFound);

        var info = Assert.Single(await _service.GetInfoForTeams([teamId], Ct));

        Assert.Null(info.ViewId);
        Assert.Null(info.ViewName);
        Assert.Null(info.TeamName);
    }

    #endregion

    #region The view's teams

    /// <summary>
    /// The other direction, and a different route: the view's teams in one call. Cached under the view's id
    /// in the same cache the team lookups use.
    /// </summary>
    [Fact]
    public async Task GetTeamsForView_ReturnsTheTeamIdsAndAsksOnce()
    {
        var viewId = Guid.NewGuid();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        _http.Answers(
            $"api/views/{viewId}/teams",
            new[]
            {
                new Team { Id = first, Name = "Red Team", ViewId = viewId },
                new Team { Id = second, Name = "Blue Team", ViewId = viewId },
            });

        var teams = await _service.GetTeamsForView(viewId, Ct);
        var again = await _service.GetTeamsForView(viewId, Ct);

        Assert.Equal<Guid>([first, second], teams);
        Assert.Equal<Guid>([first, second], again);
        Assert.Equal<string>([$"api/views/{viewId}/teams"], _http.Paths);
    }

    /// <summary>
    /// A view with no teams is cached as an empty list rather than looked up again, which is worth knowing
    /// because a view is created before its teams are: the empty answer sticks for fifteen minutes.
    /// </summary>
    [Fact]
    public async Task GetTeamsForView_ForAViewWithNoTeams_CachesTheEmptyAnswer()
    {
        var viewId = Guid.NewGuid();
        _http.Answers($"api/views/{viewId}/teams", Array.Empty<Team>());

        Assert.Empty(await _service.GetTeamsForView(viewId, Ct));
        Assert.Empty(await _service.GetTeamsForView(viewId, Ct));

        Assert.Single(_http.Sent);
    }

    /// <summary>
    /// Not found is not forgiven on this route, unlike the team lookup: the caller gets the exception. Its
    /// one caller is inside the telemetry path, whose exceptions are swallowed by the hub's caller, so this
    /// asymmetry has never been visible.
    /// </summary>
    [Fact]
    public async Task GetTeamsForView_ForAViewPlayerDoesNotHave_Throws()
    {
        var viewId = Guid.NewGuid();
        _http.Answers($"api/views/{viewId}/teams", HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<ApiException>(() => _service.GetTeamsForView(viewId, Ct));
    }

    #endregion

    #region Arrangement

    /// <summary>What player.api answers for one team. The route is the generated client's.</summary>
    private void Team(Guid teamId, Guid viewId, string name = "Team") =>
        _http.Answers($"api/teams/{teamId}", new Team { Id = teamId, Name = name, ViewId = viewId });

    private void View(Guid viewId, string name) =>
        _http.Answers($"api/views/{viewId}", new View { Id = viewId, Name = name });

    #endregion
}
