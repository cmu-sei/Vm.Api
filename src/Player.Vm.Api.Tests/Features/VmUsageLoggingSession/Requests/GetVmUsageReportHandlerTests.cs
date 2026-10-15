// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Tests.Support;
using DomainEntry = Player.Vm.Api.Domain.Models.VmUsageLogEntry;
using DomainSession = Player.Vm.Api.Domain.Models.VmUsageLoggingSession;
using GetVmUsageReport = Player.Vm.Api.Features.VmUsageLoggingSession.GetVmUsageReport;
using VmUsageReport = Player.Vm.Api.Features.VmUsageLoggingSession.VmUsageReport;

namespace Player.Vm.Api.Tests.Features.VmUsageLoggingSession.Requests;

/// <summary>
/// The usage report's one permission question, asked of the handler directly over this test's usage-log
/// database with the real <c>PlayerService</c> (over a <see cref="PlayerDirectory"/>) deciding. The report
/// refuses nobody: without the system <c>ViewViews</c> it narrows to the caller's own activity, which is the
/// answer a request sees too (<c>VmUsageLoggingSessionEndpointTests</c>' report tests); here the caller holds
/// a View permission on the very View the session belongs to, so what narrows the report is the system
/// permission and nothing else.
/// </summary>
public class GetVmUsageReportHandlerTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _viewId = Guid.NewGuid();
    private readonly PlayerHarness _player = new(Guid.NewGuid(), TestContext.Current.CancellationToken);
    private ServiceProvider _framework;

    [Fact]
    public async Task GetVmUsageReport_leaves_out_other_users_activity_for_a_caller_holding_ViewView_on_the_sessions_view()
    {
        var session = await SeedSession();
        await SeedEntry(session, _player.CallerId, "me");
        await SeedEntry(session, Guid.NewGuid(), "someone else");
        _player.Directory.AddView(_viewId);
        _player.Grant(b => b.Holding(AppViewPermission.ViewView, _viewId));

        var report = await Report();

        Assert.Equal("me", Assert.Single(report).UserName);
    }

    [Fact]
    public async Task GetVmUsageReport_includes_everyones_activity_for_a_caller_holding_exactly_ViewViews()
    {
        var session = await SeedSession();
        await SeedEntry(session, _player.CallerId, "me");
        await SeedEntry(session, Guid.NewGuid(), "someone else");
        _player.Grant(b => b.Holding(AppSystemPermission.ViewViews));

        var report = await Report();

        Assert.Equal(["me", "someone else"], report.Select(x => x.UserName));
    }

    public override async ValueTask DisposeAsync()
    {
        if (_framework is not null)
        {
            await _framework.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>The report over a window holding the seeded session, as the caller.</summary>
    private async Task<List<VmUsageReport>> Report()
    {
        await using var context = NewLoggingContext();
        var handler = new GetVmUsageReport.Handler(
            context, TestMapper.Mapper, FrameworkAuthorization(), _player.User, _player.Service);

        return await handler.Handle(
            new GetVmUsageReport.Query { ReportStart = Noon.AddDays(-1), ReportEnd = Noon.AddDays(1) }, Ct);
    }

    /// <summary>
    /// The framework's own authorization service, which production injects into the handler. The handler
    /// asks it nothing: every decision is <c>PlayerService</c>'s.
    /// </summary>
    private IAuthorizationService FrameworkAuthorization()
    {
        _framework ??= new ServiceCollection().AddLogging().AddAuthorization().BuildServiceProvider();

        return _framework.GetRequiredService<IAuthorizationService>();
    }

    private async Task<DomainSession> SeedSession()
    {
        var session = new DomainSession
        {
            ViewId = _viewId,
            TeamIds = [Guid.NewGuid()],
            SessionName = "session",
            CreatedDt = Noon,
            SessionStart = Noon,
            SessionEnd = Noon.AddHours(1),
        };

        await using var context = NewLoggingContext();
        context.VmUsageLoggingSessions.Add(session);
        await context.SaveChangesAsync(Ct);

        return session;
    }

    private async Task SeedEntry(DomainSession session, Guid userId, string userName)
    {
        await using var context = NewLoggingContext();
        context.VmUsageLogEntries.Add(new DomainEntry
        {
            SessionId = session.Id,
            VmId = Guid.NewGuid(),
            VmName = "vm",
            IpAddress = "10.0.0.1",
            UserId = userId,
            UserName = userName,
            VmActiveDT = Noon,
            VmInactiveDT = Noon.AddMinutes(10),
        });
        await context.SaveChangesAsync(Ct);
    }
}
