// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Newtonsoft.Json.Linq;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Options;
using Xunit;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

public class XApiServiceTests
{
    private const string ActiveTeamExtension = "https://crucible.sei.cmu.edu/xapi/extensions/active-team-ids";

    [Fact]
    public async Task TrackVmActionsAsync_WhenConfigured_QueuesStatements()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var vmId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var options = BuildOptions();
        var viewService = BuildViewService((teamId, "Blue Team", viewId));

        await using var context = BuildContext();

        context.Vms.Add(new VmEntity
        {
            Id = vmId,
            Name = "web-01",
            Type = VmType.Proxmox,
            VmTeams =
            [
                new VmTeam(teamId, vmId)
            ]
        });
        await context.SaveChangesAsync(cancellationToken);

        var queue = new XApiQueueService(context, NullLogger<XApiQueueService>.Instance);
        var service = BuildService(context, viewService, options, queue, "Test User", subject);

        await service.TrackConsoleOpenedAsync(vmId, [teamId], cancellationToken);
        await service.TrackConsoleClosedAsync(vmId, [teamId], cancellationToken);
        await service.TrackPowerOperationAsync(vmId, PowerOperation.PowerOn, cancellationToken);
        await service.TrackIsoUploadedAsync(viewId, "view", "training.iso", cancellationToken);
        await service.TrackUserFollowedAsync(Guid.NewGuid(), "Observed User", viewId, teamId, cancellationToken);
        await service.TrackUserUnfollowedAsync(Guid.NewGuid(), "Observed User", viewId, cancellationToken);

        var statements = context.XApiQueuedStatements.ToArray();
        Assert.Equal(6, statements.Length);

        var openedStatement = statements.Single(statement => statement.Verb == "console-opened");
        var openedStatementJson = JObject.Parse(openedStatement.StatementJson);
        var team = openedStatementJson["context"]?["team"];

        // The actor's team is per statement, so it rides on context.team and never on the shared
        // console activity definition.
        Assert.Equal("Group", team?["objectType"]?.Value<string>());
        Assert.Equal("Blue Team", team?["name"]?.Value<string>());
        Assert.Equal(teamId.ToString(), team?["account"]?["name"]?.Value<string>());
        Assert.Equal("https://player-ui.example.test/", team?["account"]?["homePage"]?.Value<string>());
        Assert.Equal(
            subject.ToString(),
            Assert.Single(team?["member"] as JArray)["account"]?["name"]?.Value<string>());
        Assert.Null(openedStatementJson["object"]?["definition"]?["extensions"]?[ActiveTeamExtension]);

        var closedStatement = Assert.Single(statements, statement => statement.Verb == "console-closed");
        Assert.Equal(XApiQueueStatus.Pending, closedStatement.Status);
        Assert.Equal($"https://vm.example.test/api/vms/{vmId}/console", closedStatement.ActivityId);
        Assert.Equal(viewId, closedStatement.ViewId);

        Assert.Equal(XApiQueueStatus.Pending, openedStatement.Status);
        Assert.Equal($"https://vm.example.test/api/vms/{vmId}/console", openedStatement.ActivityId);
        Assert.Equal(viewId, openedStatement.ViewId);

        var powerStatement = Assert.Single(statements, statement => statement.Verb == "power-on");
        Assert.Equal($"https://vm.example.test/api/vms/{vmId}/actions/power-on", powerStatement.ActivityId);
        Assert.Equal(viewId, powerStatement.ViewId);

        // Only the console path is told the team the actor was working as. The rest know the VM's own
        // teams, which are not the same thing, so they carry no context.team.
        Assert.Null(JObject.Parse(powerStatement.StatementJson)["context"]?["team"]);

        var isoStatement = Assert.Single(statements, statement => statement.Verb == "iso-uploaded");
        Assert.Equal(
            $"https://vm.example.test/api/views/{viewId}/isos/training.iso",
            isoStatement.ActivityId);
        Assert.Equal(viewId, isoStatement.ViewId);

        var followedStatement = Assert.Single(statements, statement => statement.Verb == "followed");
        Assert.Equal(viewId, followedStatement.ViewId);

        var unfollowedStatement = Assert.Single(statements, statement => statement.Verb == "unfollowed");
        Assert.Equal(viewId, unfollowedStatement.ViewId);
    }

    [Fact]
    public async Task TrackConsoleOpenedAsync_ForActorsOnDifferentTeams_KeepsOneObjectDefinitionAndSeparateContextTeams()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var vmId = Guid.NewGuid();
        var firstTeamId = Guid.NewGuid();
        var secondTeamId = Guid.NewGuid();
        var options = BuildOptions();
        var viewService = BuildViewService(
            (firstTeamId, "Blue Team", Guid.NewGuid()),
            (secondTeamId, "Red Team", Guid.NewGuid()));

        await using var context = BuildContext();

        context.Vms.Add(new VmEntity
        {
            Id = vmId,
            Name = "web-01",
            Type = VmType.Proxmox,
            VmTeams =
            [
                new VmTeam(firstTeamId, vmId),
                new VmTeam(secondTeamId, vmId)
            ]
        });
        await context.SaveChangesAsync(cancellationToken);

        var queue = new XApiQueueService(context, NullLogger<XApiQueueService>.Instance);

        await BuildService(context, viewService, options, queue, "First User", Guid.NewGuid())
            .TrackConsoleOpenedAsync(vmId, [firstTeamId], cancellationToken);
        await BuildService(context, viewService, options, queue, "Second User", Guid.NewGuid())
            .TrackConsoleOpenedAsync(vmId, [secondTeamId], cancellationToken);

        var statements = context.XApiQueuedStatements
            .Where(statement => statement.Verb == "console-opened")
            .ToArray()
            .Select(statement => JObject.Parse(statement.StatementJson))
            .OrderBy(statement => statement["actor"]?["name"]?.Value<string>())
            .ToArray();
        Assert.Equal(2, statements.Length);

        // Both participants describe the same activity, so the definition they send has to be
        // identical. Anything actor specific in there would be overwritten in the LRS by whichever
        // statement arrived last.
        Assert.Equal(
            statements[0]["object"]?["id"]?.Value<string>(),
            statements[1]["object"]?["id"]?.Value<string>());
        Assert.True(JToken.DeepEquals(
            statements[0]["object"]?["definition"],
            statements[1]["object"]?["definition"]));

        // The team each actor was on stays on their own statement.
        Assert.Equal(
            firstTeamId.ToString(),
            statements[0]["context"]?["team"]?["account"]?["name"]?.Value<string>());
        Assert.Equal("Blue Team", statements[0]["context"]?["team"]?["name"]?.Value<string>());
        Assert.Equal(
            secondTeamId.ToString(),
            statements[1]["context"]?["team"]?["account"]?["name"]?.Value<string>());
        Assert.Equal("Red Team", statements[1]["context"]?["team"]?["name"]?.Value<string>());

        // Same home page for both, or the LRS treats the two teams as accounts from different systems
        // and cannot roll them up with the teams Player API reports.
        Assert.Equal(
            "https://player-ui.example.test/",
            statements[0]["context"]?["team"]?["account"]?["homePage"]?.Value<string>());
        Assert.Equal(
            "https://player-ui.example.test/",
            statements[1]["context"]?["team"]?["account"]?["homePage"]?.Value<string>());
    }

    [Fact]
    public async Task TrackConsoleOpenedAsync_WhenActiveOnMultipleTeams_ListsThemInContextActivitiesOther()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var vmId = Guid.NewGuid();
        var firstTeamId = Guid.NewGuid();
        var secondTeamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var options = BuildOptions();
        var viewService = BuildViewService(
            (firstTeamId, "Blue Team", viewId),
            (secondTeamId, "Red Team", Guid.NewGuid()));

        await using var context = BuildContext();

        context.Vms.Add(new VmEntity
        {
            Id = vmId,
            Name = "web-01",
            Type = VmType.Proxmox,
            VmTeams =
            [
                new VmTeam(firstTeamId, vmId),
                new VmTeam(secondTeamId, vmId)
            ]
        });
        await context.SaveChangesAsync(cancellationToken);

        var queue = new XApiQueueService(context, NullLogger<XApiQueueService>.Instance);

        await BuildService(context, viewService, options, queue, "Multi Team User", Guid.NewGuid())
            .TrackConsoleOpenedAsync(vmId, [firstTeamId, secondTeamId], cancellationToken);

        var statement = JObject.Parse(Assert.Single(context.XApiQueuedStatements.ToArray()).StatementJson);

        Assert.Equal(
            firstTeamId.ToString(),
            statement["context"]?["team"]?["account"]?["name"]?.Value<string>());
        Assert.Equal(
            new[]
            {
                $"https://player.example.test/api/teams/{firstTeamId}",
                $"https://player.example.test/api/teams/{secondTeamId}"
            },
            statement["context"]?["contextActivities"]?["other"]
                ?.Select(activity => activity["id"]?.Value<string>())
                .ToArray());
    }

    [Fact]
    public async Task TrackConsoleOpenedAsync_WhenFirstTeamHasNoView_NamesTheTeamTheRegistrationBelongsTo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var vmId = Guid.NewGuid();
        var unknownTeamId = Guid.NewGuid();
        var knownTeamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var options = BuildOptions();

        // Player API returns no view for a team it cannot find, and swallows the 404. The team is
        // still in the actor's active set, so context.team has to skip past it to the team whose view
        // ends up on registration, or the two describe different teams.
        var viewService = BuildViewService(
            (unknownTeamId, null, null),
            (knownTeamId, "Blue Team", viewId));

        await using var context = BuildContext();

        context.Vms.Add(new VmEntity
        {
            Id = vmId,
            Name = "web-01",
            Type = VmType.Proxmox,
            VmTeams =
            [
                new VmTeam(knownTeamId, vmId)
            ]
        });
        await context.SaveChangesAsync(cancellationToken);

        var queue = new XApiQueueService(context, NullLogger<XApiQueueService>.Instance);

        await BuildService(context, viewService, options, queue, "Test User", Guid.NewGuid())
            .TrackConsoleOpenedAsync(vmId, [unknownTeamId, knownTeamId], cancellationToken);

        var queued = Assert.Single(context.XApiQueuedStatements.ToArray());
        var statement = JObject.Parse(queued.StatementJson);

        Assert.Equal(viewId, queued.ViewId);
        Assert.Equal(viewId.ToString(), statement["context"]?["registration"]?.Value<string>());
        Assert.Equal(
            knownTeamId.ToString(),
            statement["context"]?["team"]?["account"]?["name"]?.Value<string>());
        Assert.Equal("Blue Team", statement["context"]?["team"]?["name"]?.Value<string>());
        Assert.Equal(
            $"https://player.example.test/api/views/{viewId}",
            statement["context"]?["contextActivities"]?["parent"]?[0]?["id"]?.Value<string>());
    }

    [Fact]
    public async Task TrackConsoleOpenedAsync_WhenPlayerUiUrlIsMissing_OmitsContextTeamAndStillQueues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var vmId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var options = BuildOptions();
        options.PlayerUiUrl = null;
        var viewService = BuildViewService((teamId, "Blue Team", viewId));

        await using var context = BuildContext();

        context.Vms.Add(new VmEntity
        {
            Id = vmId,
            Name = "web-01",
            Type = VmType.Proxmox,
            VmTeams =
            [
                new VmTeam(teamId, vmId)
            ]
        });
        await context.SaveChangesAsync(cancellationToken);

        var queue = new XApiQueueService(context, NullLogger<XApiQueueService>.Instance);

        await BuildService(context, viewService, options, queue, "Test User", Guid.NewGuid())
            .TrackConsoleOpenedAsync(vmId, [teamId], cancellationToken);

        // A missing home page costs the team, not the statement.
        var statement = JObject.Parse(Assert.Single(context.XApiQueuedStatements.ToArray()).StatementJson);
        Assert.Null(statement["context"]?["team"]);
        Assert.Equal(viewId.ToString(), statement["context"]?["registration"]?.Value<string>());
    }

    [Fact]
    public void IsConfigured_WhenEndpointOrCredentialsAreMissing_ReturnsFalse()
    {
        Assert.False(XApiService.IsConfigured(new XApiOptions
        {
            Enabled = true,
            ApiUrl = "https://vm.example.test/api",
            PlayerApiUrl = "https://player.example.test/api"
        }));
    }

    private static XApiOptions BuildOptions() =>
        new()
        {
            Enabled = true,
            Endpoint = "https://lrs.example.test/xapi",
            Username = "xapi-user",
            Password = "xapi-password",
            IssuerUrl = "https://identity.example.test/realms/crucible",
            ApiUrl = "https://vm.example.test/api",
            PlayerApiUrl = "https://player.example.test/api",

            // A different host to the Player API on purpose. The UI and the API are separate
            // applications, so a fixture that gives them one host makes it unclear which option a
            // team account was built from.
            PlayerUiUrl = "https://player-ui.example.test",
            Platform = "Crucible"
        };

    private static VmContext BuildContext() =>
        new(new DbContextOptionsBuilder<VmContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // A null view id is a team Player API could not find. ViewService reports that as a TeamInfo with
    // no view rather than throwing, so the tests have to be able to describe it.
    private static IViewService BuildViewService(params (Guid TeamId, string TeamName, Guid? ViewId)[] teams)
    {
        var viewService = Substitute.For<IViewService>();

        viewService.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(teams
                .Where(team => team.ViewId.HasValue)
                .Select(team => team.ViewId.Value)
                .Distinct()
                .ToArray()));

        foreach (var team in teams)
        {
            viewService.GetInfoForTeams(
                    Arg.Is<IEnumerable<Guid>>(teamIds => teamIds.Contains(team.TeamId)),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new[]
                {
                    new TeamInfo { TeamName = team.TeamName, ViewId = team.ViewId }
                }));
        }

        return viewService;
    }

    private static XApiService BuildService(
        VmContext context,
        IViewService viewService,
        XApiOptions options,
        XApiQueueService queue,
        string userName,
        Guid subject) =>
        new(
            context,
            viewService,
            new ClaimsPrincipal(
                new ClaimsIdentity(
                [
                    new Claim("sub", subject.ToString()),
                    new Claim("name", userName)
                ],
                "test")),
            options,
            queue,
            NullLogger<XApiService>.Instance);
}
