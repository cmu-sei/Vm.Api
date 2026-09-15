// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Player.Vm.Api.Data;
using Player.Vm.Api.Features.VmUsageLoggingSession;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
// The feature namespace and its two view models share their names with the domain entities behind them,
// which is why the API's own code writes Domain.Models.VmUsageLoggingSession everywhere. Aliases are
// clearer than the qualification.
using DomainEntry = Player.Vm.Api.Domain.Models.VmUsageLogEntry;
using DomainSession = Player.Vm.Api.Domain.Models.VmUsageLoggingSession;
using SessionView = Player.Vm.Api.Features.VmUsageLoggingSession.VmUsageLoggingSession;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The nine routes of <see cref="VmUsageLoggingSessionController"/>, driven over HTTP against a host
/// running with <c>VmUsageLogging:Enabled</c> true.
/// </summary>
/// <remarks>
/// <para>
/// Real here: the routes and their precedence, model binding, the <c>[AllowAnonymous]</c> decision, the
/// MediatR pipeline, the handlers, AutoMapper, the CSV and report projections, and EF Core against a
/// real PostgreSQL usage log database of this test's own. The usage log is a second database with its own
/// migration history - see <see cref="DatabaseFixture"/> - reachable from a test through
/// <see cref="DatabaseTestBase.NewLoggingContext"/>.
/// </para>
/// <para>
/// Substituted: <c>IPlayerService</c>, which is player.api. Every authorization decision on this
/// controller is player.api's, so what these tests assert is which question each route asks and what it
/// does with the answer.
/// </para>
/// <para>
/// Two things set this controller apart from the other seven, and both are asserted rather than fixed.
/// It is <c>[AllowAnonymous]</c>, so none of its routes answer 401 and every gate is a handler's; and
/// every action but <c>GetIsLoggingEnabled</c> is wrapped in <c>if (_options.Enabled)</c>, whose other
/// branch lives in <see cref="VmUsageLoggingDisabledEndpointTests"/> because it is chosen when the host
/// starts, not per request.
/// </para>
/// </remarks>
public class VmUsageLoggingSessionEndpointTests(
    DatabaseFixture fixture,
    VmUsageLoggingEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmUsageLoggingEnabledFactory>
{
    internal const string BaseRoute = "/api/vmusageloggingsessions";

    /// <summary>
    /// A fixed, whole-second instant. PostgreSQL's <c>timestamp with time zone</c> keeps microseconds
    /// where <see cref="DateTimeOffset"/> keeps 100-nanosecond ticks, so a value taken from the clock does
    /// not survive a round trip exactly. Everything seeded here is derived from this.
    /// </summary>
    private static readonly DateTimeOffset Noon = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _viewId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();

    /// <summary>
    /// This test's usage log database. Distinct from <see cref="DatabaseTestBase.Db"/>, which is the
    /// application database: no table in one exists in the other.
    /// </summary>
    private VmLoggingContext LoggingDb { get; set; }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        LoggingDb = NewLoggingContext();

        Factory.PlayerApi.ClearSubstitute();
        Factory.AllowEverything();
    }

    public override async ValueTask DisposeAsync()
    {
        if (LoggingDb is not null)
        {
            await LoggingDb.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    #region The route table

    private const string ReportSuffix = "/report";

    /// <summary>
    /// Every route on the controller, as an HTTP method and the suffix under
    /// <c>api/vmusageloggingsessions</c>, with <c>{id}</c> standing in for a session id the test seeds.
    /// The theories below are all over this, so a route missing from it is a route with no authorization
    /// test at all - which is what <see cref="TheRouteTable_CoversEveryActionOnTheController"/> is for.
    /// </summary>
    private static readonly (string Method, string Suffix)[] Routes =
    [
        ("GET", "/isloggingenabled"),
        ("GET", ""),
        ("GET", "/{id}"),
        ("GET", "/{id}/download"),
        ("GET", ReportSuffix),
        ("POST", ""),
        ("POST", "/{id}/endsession"),
        ("PUT", "/{id}"),
        ("DELETE", "/{id}"),
    ];

    public static TheoryData<string, string> EveryRoute => AsTheoryData(Routes);

    /// <summary>
    /// The eight routes an unauthenticated caller can actually get an answer out of. The report is the
    /// ninth - see <see cref="Report_WithoutCredentials_Is500"/>.
    /// </summary>
    public static TheoryData<string, string> EveryRouteButTheReport =>
        AsTheoryData(Routes.Where(x => x.Suffix != ReportSuffix));

    private static TheoryData<string, string> AsTheoryData(
        IEnumerable<(string Method, string Suffix)> routes)
    {
        var data = new TheoryData<string, string>();

        foreach (var (method, suffix) in routes)
        {
            data.Add(method, suffix);
        }

        return data;
    }

    /// <summary>
    /// Keeps the table honest. Without this, a route added to the controller silently opts out of every
    /// theory below and nothing goes red.
    /// </summary>
    [Fact]
    public void TheRouteTable_CoversEveryActionOnTheController()
    {
        var actions = typeof(VmUsageLoggingSessionController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(x => x.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Select(x => x.Name)
            .ToArray();

        Assert.Equal(actions.Length, EveryRoute.Count);
    }

    #endregion

    #region The gate every route shares

    /// <summary>
    /// The controller is <c>[AllowAnonymous]</c>, so no route on it challenges a caller who presents no
    /// credentials: every request reaches a handler.
    /// </summary>
    /// <remarks>
    /// Asserted rather than fixed. Every other controller in the API is <c>[Authorize]</c>, and what
    /// stands in for it here is each handler's own call to player.api - which is made with no user token
    /// on an anonymous request, so in a real deployment the answer comes back "no". That makes the
    /// anonymity harmless in practice and load-bearing in these tests, which substitute player.api: it is
    /// why nothing below needs to distinguish <see cref="ApiTestBase.Client"/> from
    /// <see cref="ApiTestBase.AnonymousClient"/>.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryRoute_IsNotChallengedWithoutCredentials(string method, string suffix)
    {
        var session = await SeedSession();

        var response = await Send(method, suffix, session.Id, AnonymousClient);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(EveryRouteButTheReport))]
    public async Task EveryRouteButTheReport_AnsweredWithoutAuthenticating(string method, string suffix)
    {
        var session = await SeedSession();

        var response = await Send(method, suffix, session.Id, AnonymousClient);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{method} {suffix} answered {(int)response.StatusCode} for a caller with no credentials");
    }

    /// <summary>
    /// The report is the one route on this <c>[AllowAnonymous]</c> controller that an anonymous caller
    /// cannot use: its handler reads the caller's id in its constructor, and
    /// <c>ClaimsPrincipalExtensions.GetId</c> hands <c>Guid.Parse</c> a null when neither the <c>sub</c>
    /// nor the <c>nameidentifier</c> claim is there. The <c>catch</c> around the first parse does not
    /// cover the second, so the request ends as a 500.
    /// </summary>
    /// <remarks>
    /// Asserted rather than fixed. The route is unusable anonymously either way - with no identity there
    /// is nothing to narrow the report to - so the repair is a decision about what it should answer
    /// instead, either a challenge or an empty report, and that belongs to whoever owns the feature.
    /// </remarks>
    [Fact]
    public async Task Report_WithoutCredentials_Is500()
    {
        var response = await AnonymousClient.GetAsync($"{BaseRoute}{ReportSuffix}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Value cannot be null. (Parameter 'input')", await Title(response));
    }

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryRoute_AnsweredWhenAuthenticated(string method, string suffix)
    {
        var session = await SeedSession();

        var response = await Send(method, suffix, session.Id);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{method} {suffix} answered {(int)response.StatusCode}");
    }

    /// <summary>
    /// <c>report</c> is a literal segment on the same template as <c>{id}</c>, so routing has to prefer
    /// it. If it did not, the route would bind "report" as a Guid and answer 400.
    /// </summary>
    [Fact]
    public async Task Report_IsNotSwallowedByTheIdRoute()
    {
        var response = await Client.GetAsync($"{BaseRoute}/report", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Get<VmUsageReport[]>(response));
    }

    [Fact]
    public async Task IsLoggingEnabled_IsNotSwallowedByTheIdRoute()
    {
        var response = await Client.GetAsync($"{BaseRoute}/isloggingenabled", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Get_WithAnIdThatIsNotAGuid_Is400()
    {
        var response = await Client.GetAsync($"{BaseRoute}/not-a-guid", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The one action outside the <c>if (_options.Enabled)</c> wrapper is also the one action that asks
    /// player.api nothing: a client needs to know whether the feature exists before it can know whether
    /// it may use it.
    /// </summary>
    [Fact]
    public async Task IsLoggingEnabled_IsAnsweredWithEveryPermissionDenied()
    {
        DenyEveryPermission();

        var response = await AnonymousClient.GetAsync($"{BaseRoute}/isloggingenabled", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", await response.Content.ReadAsStringAsync(Ct));
        await Factory.PlayerApi.DidNotReceive().Can(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<AppSystemPermission[]>(),
            Arg.Any<AppViewPermission[]>(),
            Arg.Any<AppTeamPermission[]>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region The permission each route asks for

    /// <summary>
    /// Every gated route, the permission pair its handler asks player.api for, and the message it refuses
    /// with. <c>report</c> is absent because it does not refuse - see
    /// <see cref="Report_WithoutTheSystemPermission_ReturnsOnlyTheCallersOwnActivity"/>.
    /// </summary>
    public static TheoryData<string, string, AppSystemPermission, AppViewPermission, string>
        EveryPermissionGate => new()
    {
        {
            "GET", "/{id}", AppSystemPermission.ViewViews, AppViewPermission.ViewView,
            "You do not have permission to view the specified Vm Usage Log"
        },
        {
            "GET", "", AppSystemPermission.ViewViews, AppViewPermission.ViewView,
            "You do not have permission to view Vm Usage Logs."
        },
        {
            "GET", "/{id}/download", AppSystemPermission.ViewViews, AppViewPermission.ViewView,
            "You do not have permission to view the specified Vm Usage Log"
        },
        {
            "POST", "", AppSystemPermission.ManageViews, AppViewPermission.ManageView,
            "You do not have permission to create a Vm Usage Log"
        },
        {
            "PUT", "/{id}", AppSystemPermission.ManageViews, AppViewPermission.ManageView,
            "You do not have permission to edit the specified Vm Usage Log"
        },
        {
            "DELETE", "/{id}", AppSystemPermission.ManageViews, AppViewPermission.ManageView,
            "You do not have permission to delete the specified Vm Usage Log"
        },
        {
            "POST", "/{id}/endsession", AppSystemPermission.ManageViews, AppViewPermission.ManageView,
            "You do not have permission to end the specified Vm Usage Log"
        },
    };

    [Theory]
    [MemberData(nameof(EveryPermissionGate))]
    public async Task EachRoute_IsRefusedWhenItsOwnPermissionIsDenied(
        string method,
        string suffix,
        AppSystemPermission system,
        AppViewPermission view,
        string message)
    {
        var session = await SeedSession();
        Deny(system, view);

        var response = await Send(method, suffix, session.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(message, await Title(response));
    }

    /// <summary>
    /// The other half of the pair above: denying the permission a route does <em>not</em> ask for leaves
    /// it answering normally. Without this, one handler asking for the wrong permission - a viewer able to
    /// delete, or a manager unable to read - would go unnoticed.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryPermissionGate))]
    public async Task EachRoute_IsUnaffectedWhenTheOtherPermissionIsDenied(
        string method,
        string suffix,
        AppSystemPermission system,
        AppViewPermission view,
        string message)
    {
        var session = await SeedSession();

        Deny(
            system == AppSystemPermission.ViewViews
                ? AppSystemPermission.ManageViews
                : AppSystemPermission.ViewViews,
            view == AppViewPermission.ViewView
                ? AppViewPermission.ManageView
                : AppViewPermission.ViewView);

        var response = await Send(method, suffix, session.Id);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{method} {suffix} answered {(int)response.StatusCode}");
        Assert.DoesNotContain(message, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The five routes that take a session id. All of them look the session up before asking player.api
    /// anything.
    /// </summary>
    public static TheoryData<string, string> EveryIdRoute => new()
    {
        { "GET", "/{id}" },
        { "GET", "/{id}/download" },
        { "POST", "/{id}/endsession" },
        { "PUT", "/{id}" },
        { "DELETE", "/{id}" },
    };

    /// <summary>
    /// Each id route answers 404 for an id that is not there, and does so with every permission denied.
    /// </summary>
    /// <remarks>
    /// Asserted rather than fixed. The not-found check comes first in all five handlers, so an
    /// unauthorized caller can tell a session id that exists from one that does not - the 403 only
    /// arrives once the row has been found. Reordering the two checks would be a behaviour change for
    /// clients that rely on the 404, which is why this is recorded rather than corrected.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryIdRoute))]
    public async Task EachIdRoute_ForAnUnknownId_Is404EvenWithEveryPermissionDenied(
        string method, string suffix)
    {
        DenyEveryPermission();

        var response = await Send(method, suffix, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Vm Usage Logging Session not found", await Title(response));
    }

    [Theory]
    [MemberData(nameof(EveryIdRoute))]
    public async Task EachIdRoute_AsksForPermissionOnTheStoredView(string method, string suffix)
    {
        var session = await SeedSession();

        var response = await Send(method, suffix, session.Id);

        Assert.True(response.IsSuccessStatusCode);
        await ReceivedCanForView(_viewId);
    }

    #endregion

    #region Whether logging is enabled

    [Fact]
    public async Task IsLoggingEnabled_OnAHostWithLoggingOn_IsTrue()
    {
        var response = await Client.GetAsync($"{BaseRoute}/isloggingenabled", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await Get<bool>(response));
    }

    #endregion

    #region Reading one session

    [Fact]
    public async Task Get_ReturnsTheStoredSession()
    {
        var session = await SeedSession(
            name: "quarterly exercise",
            start: Noon,
            end: Noon.AddHours(3),
            createdDt: Noon.AddDays(-1));

        var result = await Get<SessionView>(
            await Client.GetAsync($"{BaseRoute}/{session.Id}", Ct));

        Assert.Equal(session.Id, result.Id);
        Assert.Equal(_viewId, result.ViewId);
        Assert.Equal([_teamId], result.TeamIds);
        Assert.Equal("quarterly exercise", result.SessionName);
        Assert.Equal(Noon.AddDays(-1), result.CreatedDt);
        Assert.Equal(Noon, result.SessionStart);
        Assert.Equal(Noon.AddHours(3), result.SessionEnd);
    }

    /// <summary>
    /// The one asked for, not merely the only one: seeded second, so a handler that looked up the first
    /// row it found would fail this.
    /// </summary>
    [Fact]
    public async Task Get_ReturnsOnlyTheSessionAsked_For()
    {
        await SeedSession(name: "other");
        var wanted = await SeedSession(name: "wanted");

        var result = await Get<SessionView>(
            await Client.GetAsync($"{BaseRoute}/{wanted.Id}", Ct));

        Assert.Equal("wanted", result.SessionName);
    }

    #endregion

    #region Listing sessions

    [Fact]
    public async Task GetAll_WithNoSessions_IsAnEmptyList()
    {
        var result = await Get<SessionView[]>(await Client.GetAsync(BaseRoute, Ct));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAll_ReturnsEverySessionInEveryView()
    {
        var mine = await SeedSession(name: "mine");
        var theirs = await SeedSession(viewId: Guid.NewGuid(), name: "theirs");

        var result = await Get<SessionView[]>(await Client.GetAsync(BaseRoute, Ct));

        Assert.Equal(new[] { mine.Id, theirs.Id }.Order(), result.Select(x => x.Id).Order());
    }

    [Fact]
    public async Task GetAll_OrdersMostRecentlyCreatedFirst()
    {
        var oldest = await SeedSession(name: "oldest", createdDt: Noon);
        var newest = await SeedSession(name: "newest", createdDt: Noon.AddHours(2));
        var middle = await SeedSession(name: "middle", createdDt: Noon.AddHours(1));

        var result = await Get<SessionView[]>(await Client.GetAsync(BaseRoute, Ct));

        Assert.Equal([newest.Id, middle.Id, oldest.Id], result.Select(x => x.Id));
    }

    /// <summary>
    /// Asking for nothing in particular means every session, ended ones included: the controller defaults
    /// <c>onlyActive</c> to false rather than to true.
    /// </summary>
    [Fact]
    public async Task GetAll_WithNoOnlyActive_IncludesASessionThatHasEnded()
    {
        var finished = await SeedSession(name: "finished", end: Noon.AddHours(1));

        var result = await Get<SessionView[]>(await Client.GetAsync(BaseRoute, Ct));

        Assert.Equal([finished.Id], result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAll_WithAViewId_ReturnsOnlyThatViewsSessions()
    {
        var wanted = await SeedSession(name: "wanted");
        await SeedSession(viewId: Guid.NewGuid(), name: "other view");

        var result = await Get<SessionView[]>(
            await Client.GetAsync($"{BaseRoute}?viewId={_viewId}", Ct));

        Assert.Equal([wanted.Id], result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAll_WithAViewId_AsksForPermissionOnThatView()
    {
        var response = await Client.GetAsync($"{BaseRoute}?viewId={_viewId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ReceivedCanForView(_viewId);
    }

    /// <summary>
    /// With no view asked for there is no view to authorize against, so the call carries an empty view
    /// list and only the system-wide permission can satisfy it.
    /// </summary>
    [Fact]
    public async Task GetAll_WithNoViewId_AsksForPermissionWithNoView()
    {
        var response = await Client.GetAsync(BaseRoute, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ReceivedCanForView(null);
    }

    [Fact]
    public async Task GetAll_OnlyActive_IncludesASessionThatHasNotEnded()
    {
        var open = await SeedSession(name: "open", end: DateTimeOffset.MinValue);

        var result = await Get<SessionView[]>(
            await Client.GetAsync($"{BaseRoute}?onlyActive=true", Ct));

        Assert.Equal([open.Id], result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAll_OnlyActive_IncludesASessionEndingInTheFuture()
    {
        var running = await SeedSession(
            name: "running", end: DateTimeOffset.UtcNow.AddYears(1));

        var result = await Get<SessionView[]>(
            await Client.GetAsync($"{BaseRoute}?onlyActive=true", Ct));

        Assert.Equal([running.Id], result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAll_OnlyActive_ExcludesASessionThatHasEnded()
    {
        await SeedSession(name: "finished", end: Noon.AddHours(1));

        var result = await Get<SessionView[]>(
            await Client.GetAsync($"{BaseRoute}?onlyActive=true", Ct));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAll_OnlyActiveFalse_IncludesASessionThatHasEnded()
    {
        var finished = await SeedSession(name: "finished", end: Noon.AddHours(1));

        var result = await Get<SessionView[]>(
            await Client.GetAsync($"{BaseRoute}?onlyActive=false", Ct));

        Assert.Equal([finished.Id], result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAll_OnlyActiveAndAViewId_AppliesBothFilters()
    {
        var wanted = await SeedSession(name: "wanted", end: DateTimeOffset.MinValue);
        await SeedSession(name: "ended in my view", end: Noon.AddHours(1));
        await SeedSession(viewId: Guid.NewGuid(), name: "open elsewhere", end: DateTimeOffset.MinValue);

        var result = await Get<SessionView[]>(
            await Client.GetAsync($"{BaseRoute}?onlyActive=true&viewId={_viewId}", Ct));

        Assert.Equal([wanted.Id], result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAll_OnlyActive_OrdersMostRecentlyCreatedFirst()
    {
        var older = await SeedSession(name: "older", createdDt: Noon, end: DateTimeOffset.MinValue);
        var newer = await SeedSession(
            name: "newer", createdDt: Noon.AddHours(1), end: DateTimeOffset.MinValue);

        var result = await Get<SessionView[]>(
            await Client.GetAsync($"{BaseRoute}?onlyActive=true", Ct));

        Assert.Equal([newer.Id, older.Id], result.Select(x => x.Id));
    }

    #endregion

    #region Creating a session

    [Fact]
    public async Task Create_StoresTheSession()
    {
        var response = await Client.PostAsync(BaseRoute, Body(new
        {
            viewId = _viewId,
            teamIds = new[] { _teamId },
            sessionName = "new session",
            sessionStart = Noon,
            sessionEnd = Noon.AddHours(2),
        }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewLoggingContext();
        var stored = await context.VmUsageLoggingSessions.SingleAsync(Ct);

        Assert.Equal(_viewId, stored.ViewId);
        Assert.Equal([_teamId], stored.TeamIds);
        Assert.Equal("new session", stored.SessionName);
        Assert.Equal(Noon, stored.SessionStart);
        Assert.Equal(Noon.AddHours(2), stored.SessionEnd);
    }

    [Fact]
    public async Task Create_ReturnsTheStoredSessionWithItsGeneratedId()
    {
        var response = await Client.PostAsync(BaseRoute, Body(new
        {
            viewId = _viewId,
            sessionName = "new session",
        }), Ct);

        var result = await Get<SessionView>(response);

        await using var context = NewLoggingContext();
        var stored = await context.VmUsageLoggingSessions.SingleAsync(Ct);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(stored.Id, result.Id);
        Assert.Equal("new session", result.SessionName);
    }

    [Fact]
    public async Task Create_PointsLocationAtTheGetRoute()
    {
        var response = await Client.PostAsync(BaseRoute, Body(new { viewId = _viewId }), Ct);
        var result = await Get<SessionView>(response);

        Assert.Equal(
            $"{BaseRoute}/{result.Id}",
            response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task Create_StampsCreatedDtWithNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var response = await Client.PostAsync(BaseRoute, Body(new { viewId = _viewId }), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        await using var context = NewLoggingContext();
        var stored = await context.VmUsageLoggingSessions.SingleAsync(Ct);

        Assert.InRange(stored.CreatedDt, before, after);
    }

    /// <summary>
    /// <c>Create.Command</c> has no <c>CreatedDt</c>, so one in the body is not bound and cannot displace
    /// the stamp above. The server owns when the session was created.
    /// </summary>
    [Fact]
    public async Task Create_IgnoresACreatedDtInTheBody()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var response = await Client.PostAsync(BaseRoute, Body(new
        {
            viewId = _viewId,
            createdDt = Noon.AddYears(-5),
        }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = NewLoggingContext();
        var stored = await context.VmUsageLoggingSessions.SingleAsync(Ct);

        Assert.InRange(stored.CreatedDt, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Create_WithAViewId_AsksForPermissionOnThatView()
    {
        var response = await Client.PostAsync(BaseRoute, Body(new { viewId = _viewId }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await ReceivedCanForView(_viewId);
    }

    /// <summary>
    /// The command's view is optional where the entity's is not, so a session created without one is
    /// stored against <see cref="Guid.Empty"/> - a session belonging to no View, which no
    /// <c>?viewId=</c> filter will ever return.
    /// </summary>
    /// <remarks>
    /// Asserted rather than fixed, and worth reading alongside the permission call: creating it needs
    /// only the system-wide <c>ManageViews</c>, because there is no view to authorize against.
    /// </remarks>
    [Fact]
    public async Task Create_WithNoViewId_StoresTheSessionAgainstTheEmptyGuid()
    {
        var response = await Client.PostAsync(BaseRoute, Body(new { sessionName = "no view" }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await ReceivedCanForView(null);

        await using var context = NewLoggingContext();
        var stored = await context.VmUsageLoggingSessions.SingleAsync(Ct);

        Assert.Equal(Guid.Empty, stored.ViewId);
    }

    [Fact]
    public async Task Create_WhenRefused_StoresNothing()
    {
        Deny(AppSystemPermission.ManageViews, AppViewPermission.ManageView);

        var response = await Client.PostAsync(BaseRoute, Body(new { viewId = _viewId }), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewLoggingContext();
        Assert.Empty(await context.VmUsageLoggingSessions.ToArrayAsync(Ct));
    }

    #endregion

    #region Editing a session

    [Fact]
    public async Task Edit_UpdatesTheStoredSession()
    {
        var session = await SeedSession(name: "before", start: Noon, end: Noon.AddHours(1));

        var response = await Client.PutAsync($"{BaseRoute}/{session.Id}", Body(new
        {
            viewId = _viewId,
            teamIds = new[] { _teamId },
            sessionName = "after",
            sessionStart = Noon.AddHours(4),
            sessionEnd = Noon.AddHours(5),
        }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewLoggingContext();
        var stored = await context.VmUsageLoggingSessions.SingleAsync(Ct);

        Assert.Equal("after", stored.SessionName);
        Assert.Equal(Noon.AddHours(4), stored.SessionStart);
        Assert.Equal(Noon.AddHours(5), stored.SessionEnd);
    }

    [Fact]
    public async Task Edit_ReturnsTheUpdatedSession()
    {
        var session = await SeedSession(name: "before");

        var result = await Get<SessionView>(await Client.PutAsync(
            $"{BaseRoute}/{session.Id}", Body(new { viewId = _viewId, sessionName = "after" }), Ct));

        Assert.Equal(session.Id, result.Id);
        Assert.Equal("after", result.SessionName);
    }

    /// <summary>
    /// The controller overwrites the command's id with the route's, so a body naming a different session
    /// cannot reach it.
    /// </summary>
    [Fact]
    public async Task Edit_TakesTheIdFromTheRouteNotTheBody()
    {
        // The bystander first, so that an edit which looked up the first row it found would fail this too.
        var bystander = await SeedSession(name: "bystander");
        var target = await SeedSession(name: "target");

        var response = await Client.PutAsync($"{BaseRoute}/{target.Id}", Body(new
        {
            id = bystander.Id,
            viewId = _viewId,
            sessionName = "edited",
        }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewLoggingContext();

        Assert.Equal(
            "edited",
            (await context.VmUsageLoggingSessions.SingleAsync(x => x.Id == target.Id, Ct)).SessionName);
        Assert.Equal(
            "bystander",
            (await context.VmUsageLoggingSessions.SingleAsync(x => x.Id == bystander.Id, Ct)).SessionName);
    }

    [Fact]
    public async Task Edit_LeavesCreatedDtAlone()
    {
        var session = await SeedSession(createdDt: Noon.AddDays(-3));

        var response = await Client.PutAsync(
            $"{BaseRoute}/{session.Id}", Body(new { viewId = _viewId, sessionName = "after" }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewLoggingContext();
        var stored = await context.VmUsageLoggingSessions.SingleAsync(Ct);

        // Both, so that an edit which never landed cannot pass this by leaving everything alone.
        Assert.Equal("after", stored.SessionName);
        Assert.Equal(Noon.AddDays(-3), stored.CreatedDt);
    }

    /// <summary>
    /// An edit may move a session into a View the caller has no permission on: the gate is checked
    /// against the View the session is already in, before the body is mapped over it.
    /// </summary>
    /// <remarks>
    /// Asserted rather than fixed. Doing it the other way round - or asking about both Views - would
    /// close the hole, but it is a permission change, not a bug fix, and belongs to whoever owns the
    /// feature.
    /// </remarks>
    [Fact]
    public async Task Edit_AsksForPermissionOnTheStoredViewNotTheSubmittedOne()
    {
        var session = await SeedSession();
        var elsewhere = Guid.NewGuid();

        var response = await Client.PutAsync(
            $"{BaseRoute}/{session.Id}", Body(new { viewId = elsewhere, sessionName = "moved" }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ReceivedCanForView(_viewId);

        await using var context = NewLoggingContext();
        Assert.Equal(elsewhere, (await context.VmUsageLoggingSessions.SingleAsync(Ct)).ViewId);
    }

    [Fact]
    public async Task Edit_WhenRefused_ChangesNothing()
    {
        var session = await SeedSession(name: "before");
        Deny(AppSystemPermission.ManageViews, AppViewPermission.ManageView);

        var response = await Client.PutAsync(
            $"{BaseRoute}/{session.Id}", Body(new { viewId = _viewId, sessionName = "after" }), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewLoggingContext();
        Assert.Equal("before", (await context.VmUsageLoggingSessions.SingleAsync(Ct)).SessionName);
    }

    #endregion

    #region Ending a session

    [Fact]
    public async Task EndSession_StampsSessionEndWithNow()
    {
        var session = await SeedSession(end: DateTimeOffset.MinValue);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var response = await Client.PostAsync($"{BaseRoute}/{session.Id}/endsession", Body(new { }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewLoggingContext();
        var stored = await context.VmUsageLoggingSessions.SingleAsync(Ct);

        Assert.InRange(stored.SessionEnd, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task EndSession_ReturnsTheEndedSession()
    {
        var session = await SeedSession(end: DateTimeOffset.MinValue);

        var result = await Get<SessionView>(
            await Client.PostAsync($"{BaseRoute}/{session.Id}/endsession", Body(new { }), Ct));

        Assert.Equal(session.Id, result.Id);
        Assert.True(result.SessionEnd > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task EndSession_ChangesNothingElse()
    {
        var session = await SeedSession(name: "keep me", start: Noon, createdDt: Noon.AddDays(-1));

        var response = await Client.PostAsync(
            $"{BaseRoute}/{session.Id}/endsession", Body(new { }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewLoggingContext();
        var stored = await context.VmUsageLoggingSessions.SingleAsync(Ct);

        // The end first, so that an ending which never happened cannot pass this by changing nothing.
        Assert.True(stored.SessionEnd > DateTimeOffset.MinValue);
        Assert.Equal("keep me", stored.SessionName);
        Assert.Equal(Noon, stored.SessionStart);
        Assert.Equal(Noon.AddDays(-1), stored.CreatedDt);
    }

    /// <summary>
    /// Ending an already-ended session moves its end forward rather than refusing.
    /// </summary>
    /// <remarks>Asserted rather than fixed: there is no guard, and adding one would be a new rule.</remarks>
    [Fact]
    public async Task EndSession_OnASessionThatHasAlreadyEnded_MovesTheEndForward()
    {
        var session = await SeedSession(end: Noon);

        var response = await Client.PostAsync($"{BaseRoute}/{session.Id}/endsession", Body(new { }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewLoggingContext();
        Assert.True((await context.VmUsageLoggingSessions.SingleAsync(Ct)).SessionEnd > Noon);
    }

    /// <summary>
    /// Seeded second, so an ending which looked up the first row it found would end the wrong session.
    /// </summary>
    [Fact]
    public async Task EndSession_OnlyEndsTheSessionAsked_For()
    {
        var bystander = await SeedSession(name: "bystander", end: DateTimeOffset.MinValue);
        var target = await SeedSession(name: "target", end: DateTimeOffset.MinValue);

        var response = await Client.PostAsync($"{BaseRoute}/{target.Id}/endsession", Body(new { }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = NewLoggingContext();

        Assert.True(
            (await context.VmUsageLoggingSessions.SingleAsync(x => x.Id == target.Id, Ct)).SessionEnd
                > DateTimeOffset.MinValue);
        Assert.Equal(
            DateTimeOffset.MinValue,
            (await context.VmUsageLoggingSessions.SingleAsync(x => x.Id == bystander.Id, Ct)).SessionEnd);
    }

    [Fact]
    public async Task EndSession_WhenRefused_LeavesTheSessionOpen()
    {
        var session = await SeedSession(end: DateTimeOffset.MinValue);
        Deny(AppSystemPermission.ManageViews, AppViewPermission.ManageView);

        var response = await Client.PostAsync($"{BaseRoute}/{session.Id}/endsession", Body(new { }), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewLoggingContext();
        Assert.Equal(
            DateTimeOffset.MinValue,
            (await context.VmUsageLoggingSessions.SingleAsync(Ct)).SessionEnd);
    }

    #endregion

    #region Deleting a session

    [Fact]
    public async Task Delete_RemovesTheSessionAndAnswers204WithNoBody()
    {
        var session = await SeedSession();

        var response = await Client.DeleteAsync($"{BaseRoute}/{session.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));

        await using var context = NewLoggingContext();
        Assert.Empty(await context.VmUsageLoggingSessions.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task Delete_LeavesOtherSessionsAlone()
    {
        // The keeper first, so that a delete which looked up the first row it found would fail this too.
        var kept = await SeedSession(name: "kept");
        var doomed = await SeedSession(name: "doomed");

        await Client.DeleteAsync($"{BaseRoute}/{doomed.Id}", Ct);

        await using var context = NewLoggingContext();
        Assert.Equal([kept.Id], (await context.VmUsageLoggingSessions.ToArrayAsync(Ct)).Select(x => x.Id));
    }

    /// <summary>
    /// The log entries go with the session. That is the database's doing - the foreign key cascades - not
    /// the handler's, which is why it is worth an assertion: without it, deleting a session would fail on
    /// a constraint violation the moment anything had been logged.
    /// </summary>
    [Fact]
    public async Task Delete_AlsoRemovesTheSessionsLogEntries()
    {
        var session = await SeedSession();
        await SeedEntry(session);
        await SeedEntry(session);

        var response = await Client.DeleteAsync($"{BaseRoute}/{session.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var context = NewLoggingContext();
        Assert.Empty(await context.VmUsageLogEntries.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task Delete_WhenRefused_KeepsTheSession()
    {
        var session = await SeedSession();
        Deny(AppSystemPermission.ManageViews, AppViewPermission.ManageView);

        var response = await Client.DeleteAsync($"{BaseRoute}/{session.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewLoggingContext();
        Assert.Single(await context.VmUsageLoggingSessions.ToArrayAsync(Ct));
    }

    #endregion

    #region Downloading the CSV

    [Fact]
    public async Task Download_IsATextCsvAttachment()
    {
        var session = await SeedSession(name: "exercise");

        var response = await Client.GetAsync($"{BaseRoute}/{session.Id}/download", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("exercise.csv", DownloadName(response));
    }

    /// <summary>
    /// The file's name, and the View the download is authorized against, both come from the session asked
    /// for. Seeded second and in another View, so a handler that looked up the first row it found would
    /// name the file wrongly and ask about the wrong View.
    /// </summary>
    [Fact]
    public async Task Download_NamesTheFileAfterTheSessionAsked_For()
    {
        await SeedSession(viewId: Guid.NewGuid(), name: "other");
        var wanted = await SeedSession(name: "wanted");

        var response = await Client.GetAsync($"{BaseRoute}/{wanted.Id}/download", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("wanted.csv", DownloadName(response));
        await ReceivedCanForView(_viewId);
    }

    /// <summary>
    /// A session saved with an empty name falls back to its id, so the file is still named something a
    /// browser can save.
    /// </summary>
    [Fact]
    public async Task Download_WithAnEmptySessionName_NamesTheFileAfterTheId()
    {
        var session = await SeedSession(name: string.Empty);

        var response = await Client.GetAsync($"{BaseRoute}/{session.Id}/download", Ct);

        Assert.Equal($"{session.Id}.csv", DownloadName(response));
    }

    /// <summary>
    /// A session saved with no name at all - which nothing prevents, the column being nullable - cannot
    /// be downloaded: the fallback above tests <c>SessionName.Length</c>, so a null name throws.
    /// </summary>
    /// <remarks>
    /// Asserted rather than fixed. The 500 is the honest report of the state the row is in, and the
    /// repair belongs where the null gets in.
    /// </remarks>
    [Fact]
    public async Task Download_WithANullSessionName_Is500()
    {
        var session = await SeedSession(name: null);

        var response = await Client.GetAsync($"{BaseRoute}/{session.Id}/download", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Download_WithNoEntries_IsJustTheHeaderRow()
    {
        var session = await SeedSession();

        var csv = await Csv(session.Id);

        Assert.Equal(
            "SessionID, LogID, VmID, VmName, IpAddress, UserId, UserName, VmActiveDateTime, "
                + "VmInactiveDateTime\r\n",
            csv);
    }

    [Fact]
    public async Task Download_HasOneRowPerLogEntry()
    {
        var session = await SeedSession();
        await SeedEntry(session, vmName: "vm-a");
        await SeedEntry(session, vmName: "vm-b");
        await SeedEntry(session, vmName: "vm-c");

        var rows = Rows(await Csv(session.Id));

        Assert.Equal(3, rows.Length);
    }

    [Fact]
    public async Task Download_CarriesEveryColumnOfAnEntry()
    {
        var session = await SeedSession();
        var entry = await SeedEntry(
            session,
            vmName: "web-01",
            ipAddress: "10.0.0.4",
            userName: "ada",
            activeAt: Noon,
            inactiveAt: Noon.AddMinutes(30));

        var row = Rows(await Csv(session.Id)).Single().Split(", ");

        Assert.Equal(session.Id.ToString(), row[0]);
        Assert.Equal(entry.Id.ToString(), row[1]);
        Assert.Equal(entry.VmId.ToString(), row[2]);
        Assert.Equal("web-01", row[3]);
        Assert.Equal("10.0.0.4", row[4]);
        Assert.Equal(entry.UserId.ToString(), row[5]);
        Assert.Equal("ada", row[6]);
        Assert.Equal(Ascii(Noon.ToString()), row[7]);
        Assert.Equal(Ascii(Noon.AddMinutes(30).ToString()), row[8]);
    }

    /// <summary>
    /// The file is encoded as ASCII, so anything outside it is written as a question mark. That is not
    /// hypothetical for the timestamps: .NET separates the time from AM/PM with a narrow no-break space,
    /// which is why every date in the file above has a question mark in it.
    /// </summary>
    /// <remarks>
    /// Asserted rather than fixed. It reaches names too - a Vm or a user with an accent in it loses the
    /// accent - and the repair is to choose an encoding, which changes every file the endpoint has ever
    /// produced. UTF-8 with a byte order mark is what a spreadsheet expects.
    /// </remarks>
    [Fact]
    public async Task Download_ReplacesAnythingOutsideAsciiWithAQuestionMark()
    {
        var session = await SeedSession();
        await SeedEntry(session, vmName: "café-01", userName: "Åsa");

        var row = Rows(await Csv(session.Id)).Single().Split(", ");

        Assert.Equal("caf?-01", row[3]);
        Assert.Equal("?sa", row[6]);
    }

    [Fact]
    public async Task Download_OrdersMostRecentlyActiveFirst()
    {
        var session = await SeedSession();
        await SeedEntry(session, vmName: "oldest", activeAt: Noon);
        await SeedEntry(session, vmName: "newest", activeAt: Noon.AddHours(2));
        await SeedEntry(session, vmName: "middle", activeAt: Noon.AddHours(1));

        var vmNames = Rows(await Csv(session.Id)).Select(x => x.Split(", ")[3]);

        Assert.Equal(["newest", "middle", "oldest"], vmNames);
    }

    [Fact]
    public async Task Download_OnlyIncludesEntriesFromThatSession()
    {
        var session = await SeedSession();
        var other = await SeedSession();
        await SeedEntry(session, vmName: "wanted");
        await SeedEntry(other, vmName: "not wanted");

        var rows = Rows(await Csv(session.Id));

        Assert.Equal("wanted", Assert.Single(rows).Split(", ")[3]);
    }

    /// <summary>
    /// A Vm reports its addresses as one comma-separated string, which would otherwise split the row into
    /// extra columns. The separator is replaced with a space so each entry stays one nine-column row.
    /// </summary>
    [Fact]
    public async Task Download_FlattensACommaSeparatedIpAddressIntoOneColumn()
    {
        var session = await SeedSession();
        await SeedEntry(session, ipAddress: "10.0.0.4, 10.0.0.5");

        var row = Rows(await Csv(session.Id)).Single();

        Assert.Equal(9, row.Split(", ").Length);
        Assert.Equal("10.0.0.4 10.0.0.5", row.Split(", ")[4]);
    }

    /// <summary>
    /// An entry with no address at all cannot be downloaded, for the same reason a session with no name
    /// cannot: the flattening above is done on the string without checking it.
    /// </summary>
    /// <remarks>Asserted rather than fixed - see <see cref="Download_WithANullSessionName_Is500"/>.</remarks>
    [Fact]
    public async Task Download_WithANullIpAddress_Is500()
    {
        var session = await SeedSession();
        await SeedEntry(session, ipAddress: null);

        var response = await Client.GetAsync($"{BaseRoute}/{session.Id}/download", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// CRLF, not LF: the file is meant to open in a spreadsheet on Windows.
    /// </summary>
    [Fact]
    public async Task Download_SeparatesRowsWithCarriageReturnLineFeed()
    {
        var session = await SeedSession();
        await SeedEntry(session);
        await SeedEntry(session);

        var csv = await Csv(session.Id);

        Assert.Equal(2, csv.Split("\r\n").Length - 1);
        Assert.DoesNotContain('\n', csv.Replace("\r\n", string.Empty));
    }

    /// <summary>
    /// The error paths are JSON even though the route declares <c>[Produces("text/csv")]</c>, because the
    /// exception middleware writes the response itself rather than going through content negotiation.
    /// </summary>
    [Fact]
    public async Task Download_WhenRefused_Is403AsJson()
    {
        var session = await SeedSession();
        Deny(AppSystemPermission.ViewViews, AppViewPermission.ViewView);

        var response = await Client.GetAsync($"{BaseRoute}/{session.Id}/download", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "You do not have permission to view the specified Vm Usage Log", await Title(response));
    }

    #endregion

    #region The usage report

    [Fact]
    public async Task Report_WithNoWindow_IsEmpty()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(1));
        await SeedEntry(session);

        var result = await Get<VmUsageReport[]>(await Client.GetAsync($"{BaseRoute}/report", Ct));

        Assert.Empty(result);
    }

    [Fact]
    public async Task Report_CarriesTheSessionTheVmAndTheUser()
    {
        var session = await SeedSession(name: "exercise", start: Noon, end: Noon.AddHours(4));
        var entry = await SeedEntry(
            session,
            vmName: "web-01",
            ipAddress: "10.0.0.4",
            userName: "ada",
            activeAt: Noon,
            inactiveAt: Noon.AddMinutes(30));

        var result = Assert.Single(await Report(Noon.AddDays(-1), Noon.AddDays(1)));

        Assert.Equal(session.Id, result.SessionId);
        Assert.Equal("exercise", result.SessionName);
        Assert.Equal(Noon, result.SessionStart);
        Assert.Equal(Noon.AddHours(4), result.SessionEnd);
        Assert.Equal(entry.VmId, result.VmId);
        Assert.Equal("web-01", result.VmName);
        Assert.Equal("10.0.0.4", result.IpAddress);
        Assert.Equal(entry.UserId, result.UserId);
        Assert.Equal("ada", result.UserName);
        Assert.Equal(30, result.MinutesActive);
    }

    [Fact]
    public async Task Report_SumsMinutesActiveAcrossAUsersEntriesOnAVm()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(6));
        var vmId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await SeedEntry(
            session, vmId: vmId, userId: userId, activeAt: Noon, inactiveAt: Noon.AddMinutes(10));
        await SeedEntry(
            session, vmId: vmId, userId: userId,
            activeAt: Noon.AddHours(1), inactiveAt: Noon.AddHours(1).AddMinutes(5));

        var result = Assert.Single(await Report(Noon.AddDays(-1), Noon.AddDays(1)));

        Assert.Equal(15, result.MinutesActive);
    }

    /// <summary>
    /// Minutes are truncated, not rounded: a nine-minute-and-fifty-second visit reports nine.
    /// </summary>
    [Fact]
    public async Task Report_TruncatesPartMinutes()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(1));
        await SeedEntry(session, activeAt: Noon, inactiveAt: Noon.AddSeconds(590));

        Assert.Equal(9, Assert.Single(await Report(Noon.AddDays(-1), Noon.AddDays(1))).MinutesActive);
    }

    [Fact]
    public async Task Report_GroupsSeparatelyPerVmAndPerUser()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(6));
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var vmA = Guid.NewGuid();
        var vmB = Guid.NewGuid();

        await SeedEntry(session, vmId: vmA, userId: alice, userName: "alice", vmName: "a");
        await SeedEntry(session, vmId: vmB, userId: alice, userName: "alice", vmName: "b");
        await SeedEntry(session, vmId: vmA, userId: bob, userName: "bob", vmName: "a");

        var result = await Report(Noon.AddDays(-1), Noon.AddDays(1));

        Assert.Equal(3, result.Length);
    }

    /// <summary>
    /// An entry for a Vm the user is still on has no inactive time yet, and is left out until it does.
    /// </summary>
    [Fact]
    public async Task Report_ExcludesAnEntryThatHasNotEnded()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(1));
        await SeedEntry(session, activeAt: Noon, inactiveAt: DateTimeOffset.MinValue);

        Assert.Empty(await Report(Noon.AddDays(-1), Noon.AddDays(1)));
    }

    [Fact]
    public async Task Report_IncludesASessionWhollyInsideTheWindow()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(1));
        await SeedEntry(session);

        Assert.Single(await Report(Noon.AddMinutes(-1), Noon.AddHours(2)));
    }

    /// <summary>
    /// The window is matched against the session's own start and end, not against when the activity
    /// happened, so a session that starts before the window is excluded outright - along with everything
    /// logged in it, including whatever falls inside.
    /// </summary>
    /// <remarks>
    /// Asserted rather than fixed. It makes the report an inventory of whole sessions rather than of
    /// activity in a period, which is a defensible reading of "usage report for a timespan"; changing it
    /// would change every number the report has ever produced.
    /// </remarks>
    [Fact]
    public async Task Report_ExcludesASessionThatStartedBeforeTheWindow()
    {
        var session = await SeedSession(start: Noon.AddHours(-1), end: Noon.AddHours(1));
        await SeedEntry(session, activeAt: Noon, inactiveAt: Noon.AddMinutes(10));

        Assert.Empty(await Report(Noon, Noon.AddHours(2)));
    }

    [Fact]
    public async Task Report_ExcludesASessionThatEndsAfterTheWindow()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(3));
        await SeedEntry(session);

        Assert.Empty(await Report(Noon.AddMinutes(-1), Noon.AddHours(2)));
    }

    /// <summary>
    /// A session that has not ended has <see cref="DateTimeOffset.MinValue"/> for its end, which is below
    /// every window's end - so a running session is always in range.
    /// </summary>
    [Fact]
    public async Task Report_IncludesASessionThatHasNotEnded()
    {
        var session = await SeedSession(start: Noon, end: DateTimeOffset.MinValue);
        await SeedEntry(session);

        Assert.Single(await Report(Noon.AddMinutes(-1), Noon.AddHours(2)));
    }

    [Fact]
    public async Task Report_OrdersByUserThenSessionThenVm()
    {
        var early = await SeedSession(name: "aardvark", start: Noon, end: Noon.AddHours(1));
        var late = await SeedSession(name: "zebra", start: Noon, end: Noon.AddHours(1));

        await SeedEntry(late, userName: "bob", vmName: "vm-1");
        await SeedEntry(early, userName: "bob", vmName: "vm-2");
        await SeedEntry(early, userName: "alice", vmName: "vm-9");
        await SeedEntry(early, userName: "alice", vmName: "vm-1");

        var result = await Report(Noon.AddDays(-1), Noon.AddDays(1));

        Assert.Equal(
            [
                ("alice", "aardvark", "vm-1"),
                ("alice", "aardvark", "vm-9"),
                ("bob", "aardvark", "vm-2"),
                ("bob", "zebra", "vm-1"),
            ],
            result.Select(x => (x.UserName, x.SessionName, x.VmName)));
    }

    /// <summary>
    /// The report's gate is the system-wide permission alone - no View, no team - which is what makes it
    /// a report across every View rather than one View's.
    /// </summary>
    [Fact]
    public async Task Report_AsksOnlyForTheSystemPermission()
    {
        var response = await Client.GetAsync(
            $"{BaseRoute}/report?reportStart={Query(Noon)}&reportEnd={Query(Noon.AddDays(1))}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.PlayerApi.Received(1).Can(
            Arg.Is<IEnumerable<Guid>>(x => !x.Any()),
            Arg.Is<IEnumerable<Guid>>(x => !x.Any()),
            Arg.Is<AppSystemPermission[]>(x => x.SequenceEqual(new[] { AppSystemPermission.ViewViews })),
            Arg.Is<AppViewPermission[]>(x => x.Length == 0),
            Arg.Is<AppTeamPermission[]>(x => x.Length == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Report_WithTheSystemPermission_ReturnsEveryonesActivity()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(1));
        await SeedEntry(session, userId: Factory.UserId, userName: "me");
        await SeedEntry(session, userId: Guid.NewGuid(), userName: "someone else");

        var result = await Report(Noon.AddDays(-1), Noon.AddDays(1));

        Assert.Equal(2, result.Length);
    }

    /// <summary>
    /// Without it, the report is still answered - narrowed to the caller's own activity rather than
    /// refused. This is the one route on the controller that degrades instead of returning 403.
    /// </summary>
    [Fact]
    public async Task Report_WithoutTheSystemPermission_ReturnsOnlyTheCallersOwnActivity()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(1));
        await SeedEntry(session, userId: Factory.UserId, userName: "me");
        await SeedEntry(session, userId: Guid.NewGuid(), userName: "someone else");

        Deny(AppSystemPermission.ViewViews, null);

        var result = await Report(Noon.AddDays(-1), Noon.AddDays(1));

        Assert.Equal("me", Assert.Single(result).UserName);
    }

    /// <summary>
    /// The narrowing is to the caller's own id, not to "an id that is not this one": a session containing
    /// only somebody else's activity comes back empty rather than falling open.
    /// </summary>
    [Fact]
    public async Task Report_WithoutTheSystemPermission_AndNoActivityOfItsOwn_IsEmpty()
    {
        var session = await SeedSession(start: Noon, end: Noon.AddHours(1));
        await SeedEntry(session, userId: Guid.NewGuid(), userName: "someone else");

        Deny(AppSystemPermission.ViewViews, null);

        Assert.Empty(await Report(Noon.AddDays(-1), Noon.AddDays(1)));
    }

    /// <summary>
    /// The report spans every View, and its only gate is system-wide, so a caller holding
    /// <c>ViewViews</c> sees activity from Views they have no permission on.
    /// </summary>
    /// <remarks>Asserted rather than fixed: it is what a system-wide permission means.</remarks>
    [Fact]
    public async Task Report_IsNotNarrowedByView()
    {
        var mine = await SeedSession(name: "mine", start: Noon, end: Noon.AddHours(1));
        var theirs = await SeedSession(
            viewId: Guid.NewGuid(), name: "theirs", start: Noon, end: Noon.AddHours(1));

        await SeedEntry(mine);
        await SeedEntry(theirs);

        var result = await Report(Noon.AddDays(-1), Noon.AddDays(1));

        Assert.Equal(["mine", "theirs"], result.Select(x => x.SessionName).Order());
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A session in <see cref="_viewId"/>, open unless given an end. Returns the stored entity, so its
    /// database-generated id is the real one.
    /// </summary>
    private async Task<DomainSession> SeedSession(
        Guid? viewId = null,
        string name = "session",
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        DateTimeOffset? createdDt = null)
    {
        var session = new DomainSession
        {
            ViewId = viewId ?? _viewId,
            TeamIds = [_teamId],
            SessionName = name,
            CreatedDt = createdDt ?? Noon,
            SessionStart = start ?? Noon,
            SessionEnd = end ?? DateTimeOffset.MinValue,
        };

        LoggingDb.Add(session);
        await LoggingDb.SaveChangesAsync(Ct);

        return session;
    }

    private async Task<DomainEntry> SeedEntry(
        DomainSession session,
        Guid? vmId = null,
        string vmName = "vm",
        string ipAddress = "10.0.0.1",
        Guid? userId = null,
        string userName = "user",
        DateTimeOffset? activeAt = null,
        DateTimeOffset? inactiveAt = null)
    {
        var entry = new DomainEntry
        {
            SessionId = session.Id,
            VmId = vmId ?? Guid.NewGuid(),
            VmName = vmName,
            IpAddress = ipAddress,
            UserId = userId ?? Guid.NewGuid(),
            UserName = userName,
            VmActiveDT = activeAt ?? Noon,
            VmInactiveDT = inactiveAt ?? (activeAt ?? Noon).AddMinutes(10),
        };

        LoggingDb.Add(entry);
        await LoggingDb.SaveChangesAsync(Ct);

        return entry;
    }

    /// <summary>
    /// Drives one row of <see cref="EveryRoute"/>. The bodies are the emptiest thing that binds, because
    /// what those theories are about is the route, not the payload.
    /// </summary>
    private Task<HttpResponseMessage> Send(
        string method, string suffix, Guid id, HttpClient client = null)
    {
        client ??= Client;

        var route = BaseRoute + suffix.Replace("{id}", id.ToString());

        return method switch
        {
            "GET" => client.GetAsync(route, Ct),
            "POST" => client.PostAsync(route, Body(new { }), Ct),
            "PUT" => client.PutAsync(route, Body(new { }), Ct),
            "DELETE" => client.DeleteAsync(route, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unhandled method"),
        };
    }

    private static StringContent Body(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private async Task<T> Get<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");

        return JsonSerializer.Deserialize<T>(
            await response.Content.ReadAsStringAsync(Ct), JsonOptions);
    }

    /// <summary>The <c>ProblemDetails</c> title, which is where the exception middleware puts the message.</summary>
    private async Task<string> Title(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return problem.RootElement.GetProperty("title").GetString();
    }

    /// <summary>
    /// The name the download is offered under. Which of the two <c>Content-Disposition</c> filename
    /// parameters carries it is <c>FileContentResult</c>'s business, not this test's.
    /// </summary>
    private static string DownloadName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;

        return (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
    }

    private async Task<string> Csv(Guid sessionId)
    {
        var response = await Client.GetAsync($"{BaseRoute}/{sessionId}/download", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>The data rows of a CSV, without the header and without a trailing empty line.</summary>
    private static string[] Rows(string csv) =>
        csv.Split("\r\n").Skip(1).Where(x => x.Length > 0).ToArray();

    /// <summary>
    /// What the download does to a string, so an expected value can be written as the API writes it. See
    /// <see cref="Download_ReplacesAnythingOutsideAsciiWithAQuestionMark"/>.
    /// </summary>
    private static string Ascii(string value) =>
        Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(value));

    private async Task<VmUsageReport[]> Report(DateTimeOffset start, DateTimeOffset end) =>
        await Get<VmUsageReport[]>(await Client.GetAsync(
            $"{BaseRoute}/report?reportStart={Query(start)}&reportEnd={Query(end)}", Ct));

    private static string Query(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    /// <summary>
    /// Denies exactly one permission pair, leaving everything else allowed, so a refusal names the
    /// permission the route actually asked for. Pass a null view permission for a route that asks for the
    /// system permission alone.
    /// </summary>
    private void Deny(AppSystemPermission system, AppViewPermission? view)
    {
        // Split out because an expression tree cannot contain an "is null" pattern.
        var asksForNoView = !view.HasValue;
        var viewPermission = view.GetValueOrDefault();

        Factory.PlayerApi.Can(
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Is<AppSystemPermission[]>(x => x.Contains(system)),
                Arg.Is<AppViewPermission[]>(x =>
                    asksForNoView ? x.Length == 0 : x.Contains(viewPermission)),
                Arg.Any<AppTeamPermission[]>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
    }

    private void DenyEveryPermission()
    {
        Factory.PlayerApi.Can(
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<AppSystemPermission[]>(),
                Arg.Any<AppViewPermission[]>(),
                Arg.Any<AppTeamPermission[]>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
    }

    /// <summary>
    /// Asserts player.api was asked about exactly one View - or, for a null, about no View at all, which
    /// only the system-wide permission can answer.
    /// </summary>
    private Task ReceivedCanForView(Guid? viewId) =>
        Factory.PlayerApi.Received(1).Can(
            Arg.Is<IEnumerable<Guid>>(x => !x.Any()),
            Arg.Is<IEnumerable<Guid>>(x =>
                viewId.HasValue ? x.SequenceEqual(new[] { viewId.Value }) : !x.Any()),
            Arg.Any<AppSystemPermission[]>(),
            Arg.Any<AppViewPermission[]>(),
            Arg.Any<AppTeamPermission[]>(),
            Arg.Any<CancellationToken>());

    #endregion
}
