// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Tests.Support;
using Create = Player.Vm.Api.Features.VmUsageLoggingSession.Create;
using DomainSession = Player.Vm.Api.Domain.Models.VmUsageLoggingSession;
using Edit = Player.Vm.Api.Features.VmUsageLoggingSession.Edit;
using EndSession = Player.Vm.Api.Features.VmUsageLoggingSession.EndSession;

namespace Player.Vm.Api.Tests.Features.VmUsageLoggingSession.Requests;

/// <summary>
/// The usage-log write handlers, constructed directly over this test's usage-log database and handed a
/// token that is already cancelled, which is what a request the caller abandoned carries by the time the
/// handler writes. The caller holds <c>ManageViews</c>, so every permission question passes.
/// </summary>
public class WriteHandlerCancellationTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly PlayerHarness _player = new(Guid.NewGuid(), TestContext.Current.CancellationToken);

    /// <summary>A create handed a cancelled token stores the session.</summary>
    [Fact]
    public async Task Create_WithACancelledToken_StoresTheSession()
    {
        var handler = new Create.Handler(Logging, TestMapper.Mapper, Substitute.For<IAuthorizationService>(), Caller());

        await handler.Handle(new Create.Command { ViewId = Guid.NewGuid(), SessionName = "abandoned" }, Cancelled());

        await using var context = NewLoggingContext();
        Assert.Equal("abandoned", (await context.VmUsageLoggingSessions.SingleAsync(Ct)).SessionName);
    }

    /// <summary>An edit handed a cancelled token stores the edit.</summary>
    [Fact]
    public async Task Edit_WithACancelledToken_StoresTheEdit()
    {
        var session = await SeedSession();
        var handler = new Edit.Handler(Logging, TestMapper.Mapper, Substitute.For<IAuthorizationService>(), Caller());

        await handler.Handle(
            new Edit.Command { Id = session.Id, ViewId = session.ViewId, SessionName = "edited" }, Cancelled());

        await using var context = NewLoggingContext();
        Assert.Equal("edited", (await context.VmUsageLoggingSessions.SingleAsync(Ct)).SessionName);
    }

    /// <summary>Ending a session with a cancelled token stores the end.</summary>
    [Fact]
    public async Task EndSession_WithACancelledToken_StoresTheEnd()
    {
        var session = await SeedSession();
        var handler = new EndSession.Handler(Logging, TestMapper.Mapper, Substitute.For<IAuthorizationService>(), Caller());

        await handler.Handle(new EndSession.Command { Id = session.Id }, Cancelled());

        await using var context = NewLoggingContext();
        Assert.True((await context.VmUsageLoggingSessions.SingleAsync(Ct)).SessionEnd > Noon);
    }

    private Player.Vm.Api.Data.VmLoggingContext _logging;

    /// <summary>The handler's own context, disposed with the test.</summary>
    private Player.Vm.Api.Data.VmLoggingContext Logging => _logging ??= NewLoggingContext();

    public override async ValueTask DisposeAsync()
    {
        if (_logging is not null)
        {
            await _logging.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private IPlayerService Caller()
    {
        _player.Grant(b => b.Holding(AppSystemPermission.ManageViews));
        return _player.Service;
    }

    private static CancellationToken Cancelled() => new(canceled: true);

    private async Task<DomainSession> SeedSession()
    {
        var session = new DomainSession
        {
            ViewId = Guid.NewGuid(),
            TeamIds = [Guid.NewGuid()],
            SessionName = "session",
            CreatedDt = Noon,
            SessionStart = Noon,
            SessionEnd = DateTimeOffset.MinValue,
        };

        await using var context = NewLoggingContext();
        context.VmUsageLoggingSessions.Add(session);
        await context.SaveChangesAsync(Ct);

        return session;
    }
}
