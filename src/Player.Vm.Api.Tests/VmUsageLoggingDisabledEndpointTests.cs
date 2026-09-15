// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The other branch of <c>VmUsageLoggingSessionController</c>'s <c>if (_options.Enabled)</c>: what the
/// nine routes answer on a host running the configuration <c>appsettings.json</c> ships, with the usage
/// log switched off.
/// </summary>
/// <remarks>
/// A second class rather than a second set of facts in
/// <see cref="VmUsageLoggingSessionEndpointTests"/>, because the flag is read when the host starts and
/// once more in the controller's constructor - see <see cref="VmUsageLoggingEnabledFactory"/>. This one
/// runs on the plain <see cref="VmApiFactory"/>, so it is also the assurance that the seven other
/// endpoint classes, which share that default, are running against the shipped configuration.
/// </remarks>
public class VmUsageLoggingDisabledEndpointTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    private const string BaseRoute = VmUsageLoggingSessionEndpointTests.BaseRoute;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        Factory.PlayerApi.ClearSubstitute();
        Factory.AllowEverything();
    }

    /// <summary>
    /// Every route the wrapper covers. The download is absent because it does not answer like the others
    /// - see <see cref="Download_WhenDisabled_Is406"/> - and <c>isloggingenabled</c> because it is outside
    /// the wrapper entirely.
    /// </summary>
    public static TheoryData<string, string> EveryGatedRoute => new()
    {
        { "GET", "" },
        { "GET", "/{id}" },
        { "GET", "/report" },
        { "POST", "" },
        { "POST", "/{id}/endsession" },
        { "PUT", "/{id}" },
        { "DELETE", "/{id}" },
    };

    /// <summary>
    /// Keeps the two tables between them accounting for every action, so a route added to the controller
    /// cannot quietly go untested on this side of the flag.
    /// </summary>
    [Fact]
    public void TheTwoTables_TogetherCoverEveryRoute()
    {
        Assert.Equal(
            VmUsageLoggingSessionEndpointTests.EveryRoute.Count,
            EveryGatedRoute.Count + 2);
    }

    [Theory]
    [MemberData(nameof(EveryGatedRoute))]
    public async Task EachGatedRoute_WhenLoggingIsDisabled_Is404(string method, string suffix)
    {
        var route = BaseRoute + suffix.Replace("{id}", Guid.NewGuid().ToString());

        var response = method switch
        {
            "GET" => await Client.GetAsync(route, Ct),
            "POST" => await Client.PostAsync(route, EmptyBody, Ct),
            "PUT" => await Client.PutAsync(route, EmptyBody, Ct),
            "DELETE" => await Client.DeleteAsync(route, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unhandled method"),
        };

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Vm Usage Logging is disabled", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The download refuses differently, and not deliberately: <c>[Produces("text/csv")]</c> narrows
    /// content negotiation to a media type no formatter can write, and the disabled branch answers with a
    /// string body rather than a file. So the caller gets 406 with nothing in it instead of the 404 the
    /// other seven routes give.
    /// </summary>
    /// <remarks>
    /// Asserted rather than fixed. It is only reachable on a host with logging off, where the caller has
    /// no usage log to download either way - but a client written against the other seven routes will not
    /// recognise it.
    /// </remarks>
    [Fact]
    public async Task Download_WhenDisabled_Is406()
    {
        var response = await Client.GetAsync($"{BaseRoute}/{Guid.NewGuid()}/download", Ct);

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    /// <summary>
    /// The one route outside the wrapper, and the one a client is expected to ask first.
    /// </summary>
    [Fact]
    public async Task IsLoggingEnabled_WhenLoggingIsDisabled_IsFalse()
    {
        var response = await Client.GetAsync($"{BaseRoute}/isloggingenabled", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("false", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The refusal is the controller's, before any handler runs, so nothing reaches the usage log.
    /// </summary>
    [Fact]
    public async Task Create_WhenDisabled_StoresNothing()
    {
        var response = await Client.PostAsync(
            BaseRoute,
            new StringContent("{}", Encoding.UTF8, "application/json"),
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var context = NewLoggingContext();
        Assert.Empty(await context.VmUsageLoggingSessions.ToArrayAsync(Ct));
    }

    /// <summary>
    /// And nothing asks player.api anything either: the flag is checked first, so a disabled host makes
    /// no authorization calls on this controller at all.
    /// </summary>
    [Fact]
    public async Task ADisabledRoute_AsksPlayerApiNothing()
    {
        var response = await Client.GetAsync(BaseRoute, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await Factory.PlayerApi.DidNotReceive().Can(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<AppSystemPermission[]>(),
            Arg.Any<AppViewPermission[]>(),
            Arg.Any<AppTeamPermission[]>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The usage log database exists and is migrated even on a disabled host, because
    /// <c>Startup</c> registers <c>VmLoggingContext</c> either way. Worth one assertion: without it, the
    /// disabled tests above would pass just as well against a harness that had never created the second
    /// database at all.
    /// </summary>
    [Fact]
    public async Task TheUsageLogDatabase_ExistsEvenWithLoggingDisabled()
    {
        await using var context = NewLoggingContext();

        context.Add(new VmUsageLoggingSession
        {
            ViewId = Guid.NewGuid(),
            SessionName = "seeded directly",
        });

        await context.SaveChangesAsync(Ct);

        Assert.Single(await context.VmUsageLoggingSessions.ToArrayAsync(Ct));
    }

    private static StringContent EmptyBody =>
        new("{}", Encoding.UTF8, "application/json");
}
