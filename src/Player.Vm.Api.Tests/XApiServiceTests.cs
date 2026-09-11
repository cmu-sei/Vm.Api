// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
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
    [Fact]
    public async Task TrackVmActionsAsync_WhenConfigured_QueuesStatements()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var vmId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var options = new XApiOptions
        {
            Enabled = true,
            Endpoint = "https://lrs.example.test/xapi",
            Username = "xapi-user",
            Password = "xapi-password",
            IssuerUrl = "https://identity.example.test/realms/crucible",
            ApiUrl = "https://vm.example.test/api",
            PlayerApiUrl = "https://player.example.test/api",
            Platform = "Crucible"
        };
        var viewService = Substitute.For<IViewService>();
        viewService.GetViewIdsForTeams(
                Arg.Any<System.Collections.Generic.IEnumerable<Guid>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[] { viewId }));

        await using var context = new VmContext(
            new DbContextOptionsBuilder<VmContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        var vm = new VmEntity
        {
            Id = vmId,
            Name = "web-01",
            Type = VmType.Proxmox,
            VmTeams =
            [
                new VmTeam(teamId, vmId)
            ]
        };
        context.Vms.Add(vm);
        await context.SaveChangesAsync(cancellationToken);

        var queue = new XApiQueueService(context, NullLogger<XApiQueueService>.Instance);
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
            [
                new Claim("sub", Guid.NewGuid().ToString()),
                new Claim("name", "Test User")
            ],
            "test"));
        var service = new XApiService(
            context,
            viewService,
            principal,
            options,
            queue,
            NullLogger<XApiService>.Instance);

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
        Assert.Equal(
            teamId.ToString(),
            openedStatementJson["object"]?["definition"]?["extensions"]?[
                "https://crucible.sei.cmu.edu/xapi/extensions/active-team-ids"]?.Value<string>());

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
    public void IsConfigured_WhenEndpointOrCredentialsAreMissing_ReturnsFalse()
    {
        Assert.False(XApiService.IsConfigured(new XApiOptions
        {
            Enabled = true,
            ApiUrl = "https://vm.example.test/api",
            PlayerApiUrl = "https://player.example.test/api"
        }));
    }
}
