// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Features.Files.Requests;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Infrastructure.Options;
using Xunit;

namespace Player.Vm.Api.Tests;

// Who may write an ISO into which folder: the upload and delete permission rules, plus the resolution
// of which folder is even being targeted.
//
// These are service-level tests because the permission check lives in IsoService.Resolve*ScopeId*Async
// rather than in the UploadIso/DeleteIso handlers, which only call it. If the gate is ever hoisted into
// the handlers - as ListViewIsos/ListAllIsos do it - these tests should move with it.
//
// Every case is written against a caller who is denied by default, so an allowed case has to say which
// permission allows it. IsoServiceMountAuthTests covers the read/mount side of the same feature.
public class IsoWriteAuthTests
{
    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TeamA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TeamB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid PrimaryTeam = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly IPlayerService _playerService = Substitute.For<IPlayerService>();
    private readonly IViewService _viewService = Substitute.For<IViewService>();
    private readonly IIsoProvider _provider = Substitute.For<IIsoProvider>();

    public IsoWriteAuthTests()
    {
        // Deny every permission unless a test grants it.
        _playerService.Can(
                Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<AppSystemPermission[]>(), Arg.Any<AppViewPermission[]>(),
                Arg.Any<AppTeamPermission[]>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // The teams under test belong to the View, and the caller has a primary team in it. Tests about
        // either of those override it.
        _playerService.IsTeamInViewAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _playerService.GetPrimaryTeamByViewIdAsync(ViewId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = PrimaryTeam, Name = "primary" });
    }

    private IsoService Service() =>
        new(_playerService, _viewService, [_provider], new IsoUploadOptions(),
            NullLogger<IsoService>.Instance);

    // A view-level claim - held over the View itself, so it applies to every team in it.
    private void AllowView(AppViewPermission permission)
    {
        _playerService.Can(
                Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<AppSystemPermission[]>(),
                Arg.Is<AppViewPermission[]>(p => p.Contains(permission)),
                Arg.Any<AppTeamPermission[]>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    // A team-level claim on one specific team. The team-scoped calls pass that team as the only
    // requested id, so matching on it keeps the grant from leaking to another team's check.
    private void AllowTeam(AppTeamPermission permission, Guid teamId)
    {
        _playerService.Can(
                Arg.Is<IEnumerable<Guid>>(ids => ids != null && ids.Contains(teamId)),
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<AppSystemPermission[]>(), Arg.Any<AppViewPermission[]>(),
                Arg.Is<AppTeamPermission[]>(p => p.Contains(permission)),
                Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private void AllowSystem(AppSystemPermission permission)
    {
        _playerService.Can(
                Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
                Arg.Is<AppSystemPermission[]>(p => p.Contains(permission)),
                Arg.Any<AppViewPermission[]>(), Arg.Any<AppTeamPermission[]>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private Task<IReadOnlyList<string>> ResolveUpload(string scope, params Guid[] teamIds) =>
        Service().ResolveUploadScopeIdsAsync(ViewId, scope, teamIds, CancellationToken.None);

    private Task<string> ResolveDelete(string scope, Guid? teamId = null) =>
        Service().ResolveDeleteScopeIdAsync(ViewId, scope, teamId, CancellationToken.None);

    // ---- Upload ----

    [Fact]
    public async Task ViewScopedUpload_WithUploadViewIsos_TargetsTheViewFolder()
    {
        AllowView(AppViewPermission.UploadViewIsos);

        Assert.Equal([ViewId.ToString()], await ResolveUpload("view"));
    }

    [Fact]
    public async Task ViewScopedUpload_WithoutUploadViewIsos_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => ResolveUpload("view"));

        Assert.Equal("You do not have permission to upload public files for this View", ex.Message);
    }

    // A team member's own permission on their own team.
    [Fact]
    public async Task TeamScopedUpload_WithUploadTeamIsosOnThatTeam_TargetsThatTeamsFolder()
    {
        AllowTeam(AppTeamPermission.UploadTeamIsos, TeamA);

        Assert.Equal([TeamA.ToString()], await ResolveUpload("team", TeamA));
    }

    // UploadViewIsos is a view-wide claim, so it authorizes uploading into any team in the View without
    // a per-team claim.
    [Fact]
    public async Task TeamScopedUpload_WithOnlyUploadViewIsos_IsAllowed()
    {
        AllowView(AppViewPermission.UploadViewIsos);

        Assert.Equal([TeamA.ToString()], await ResolveUpload("team", TeamA));
    }

    [Fact]
    public async Task TeamScopedUpload_WithNeitherPermission_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => ResolveUpload("team", TeamA));

        Assert.Equal("You do not have permission to upload files for this Team", ex.Message);
    }

    // The reason the permission is checked per team rather than once for the selection: a partially
    // permitted multi-team upload is refused outright, not silently narrowed to the permitted teams.
    [Fact]
    public async Task TeamScopedUpload_WhereOnlyOneOfTheSelectedTeamsIsPermitted_IsRefusedEntirely()
    {
        AllowTeam(AppTeamPermission.UploadTeamIsos, TeamA);

        await Assert.ThrowsAsync<ForbiddenException>(() => ResolveUpload("team", TeamA, TeamB));
    }

    [Fact]
    public async Task TeamScopedUpload_WithEverySelectedTeamPermitted_TargetsAllOfThem()
    {
        AllowTeam(AppTeamPermission.UploadTeamIsos, TeamA);
        AllowTeam(AppTeamPermission.UploadTeamIsos, TeamB);

        Assert.Equal([TeamA.ToString(), TeamB.ToString()], await ResolveUpload("team", TeamA, TeamB));
    }

    // No teams selected means the caller's own primary team - and the permission check has to be about
    // that team, not the one they asked for (they asked for none).
    [Fact]
    public async Task TeamScopedUpload_WithNoTeamsSelected_AuthorizesAndTargetsThePrimaryTeam()
    {
        AllowTeam(AppTeamPermission.UploadTeamIsos, PrimaryTeam);

        Assert.Equal([PrimaryTeam.ToString()], await ResolveUpload("team"));

        await _playerService.Received().Can(
            Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { PrimaryTeam })),
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<AppSystemPermission[]>(),
            Arg.Any<AppViewPermission[]>(), Arg.Any<AppTeamPermission[]>(),
            Arg.Any<CancellationToken>());
    }

    // A caller with no team in the View has no default target, which is a 403 rather than a null deref -
    // it is also how a system operator who is not a member lands here.
    [Fact]
    public async Task TeamScopedUpload_WithNoPrimaryTeam_IsRefused()
    {
        AllowView(AppViewPermission.UploadViewIsos);
        _playerService.GetPrimaryTeamByViewIdAsync(ViewId, Arg.Any<CancellationToken>())
            .Returns((Team)null);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => ResolveUpload("team"));

        Assert.Equal("You do not have an active team in this View", ex.Message);
    }

    // A team from another View is a bad request, not a permission failure, and is caught before any
    // permission is consulted - so it cannot be used to probe what the caller may do elsewhere.
    [Fact]
    public async Task TeamScopedUpload_ToATeamOutsideTheView_IsABadRequest_BeforeAnyPermissionCheck()
    {
        AllowView(AppViewPermission.UploadViewIsos);
        _playerService.IsTeamInViewAsync(TeamA, ViewId, Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => ResolveUpload("team", TeamA));

        Assert.Equal("The specified team is not part of this View", ex.Message);

        await _playerService.DidNotReceive().Can(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<AppSystemPermission[]>(), Arg.Any<AppViewPermission[]>(),
            Arg.Any<AppTeamPermission[]>(), Arg.Any<CancellationToken>());
    }

    // ---- Delete ----

    [Fact]
    public async Task ViewScopedDelete_WithDeleteViewIsos_TargetsTheViewFolder()
    {
        AllowView(AppViewPermission.DeleteViewIsos);

        Assert.Equal(ViewId.ToString(), await ResolveDelete("view"));
    }

    [Fact]
    public async Task ViewScopedDelete_WithoutDeleteViewIsos_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => ResolveDelete("view"));

        Assert.Equal("You do not have permission to delete public files for this View", ex.Message);
    }

    [Fact]
    public async Task TeamScopedDelete_WithDeleteTeamIsosOnThatTeam_TargetsThatTeamsFolder()
    {
        AllowTeam(AppTeamPermission.DeleteTeamIsos, TeamA);

        Assert.Equal(TeamA.ToString(), await ResolveDelete("team", TeamA));
    }

    [Fact]
    public async Task TeamScopedDelete_WithOnlyDeleteViewIsos_IsAllowed()
    {
        AllowView(AppViewPermission.DeleteViewIsos);

        Assert.Equal(TeamA.ToString(), await ResolveDelete("team", TeamA));
    }

    [Fact]
    public async Task TeamScopedDelete_WithNeitherPermission_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => ResolveDelete("team", TeamA));

        Assert.Equal("You do not have permission to delete files for this Team", ex.Message);
    }

    // Rights on one team are not rights on another.
    [Fact]
    public async Task TeamScopedDelete_WithDeleteTeamIsosOnADifferentTeam_IsRefused()
    {
        AllowTeam(AppTeamPermission.DeleteTeamIsos, TeamB);

        await Assert.ThrowsAsync<ForbiddenException>(() => ResolveDelete("team", TeamA));
    }

    // The system DeleteIsos permission is the "all views" management mode: the only way to delete an ISO
    // in a View or team the caller holds no specific Delete*Isos permission for. Worth pinning, since it
    // is the one permission here that reaches another tenant's files.
    [Fact]
    public async Task ViewScopedDelete_WithSystemDeleteIsosAlone_IsAllowed()
    {
        AllowSystem(AppSystemPermission.DeleteIsos);

        Assert.Equal(ViewId.ToString(), await ResolveDelete("view"));
    }

    [Fact]
    public async Task TeamScopedDelete_WithSystemDeleteIsosAlone_IsAllowed()
    {
        AllowSystem(AppSystemPermission.DeleteIsos);

        Assert.Equal(TeamA.ToString(), await ResolveDelete("team", TeamA));
    }

    // DeleteIsos widens whose files may be deleted, not which ids are coherent: a team that is not in
    // the named View is still a bad request.
    [Fact]
    public async Task TeamScopedDelete_WithSystemDeleteIsos_StillRejectsATeamOutsideTheView()
    {
        AllowSystem(AppSystemPermission.DeleteIsos);
        _playerService.IsTeamInViewAsync(TeamA, ViewId, Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => ResolveDelete("team", TeamA));

        Assert.Equal("The specified team is not part of this View", ex.Message);
    }

    [Fact]
    public async Task TeamScopedDelete_WithNoTeamGiven_AuthorizesAndTargetsThePrimaryTeam()
    {
        AllowTeam(AppTeamPermission.DeleteTeamIsos, PrimaryTeam);

        Assert.Equal(PrimaryTeam.ToString(), await ResolveDelete("team"));
    }

    [Fact]
    public async Task TeamScopedDelete_WithNoPrimaryTeam_IsRefused()
    {
        AllowView(AppViewPermission.DeleteViewIsos);
        _playerService.GetPrimaryTeamByViewIdAsync(ViewId, Arg.Any<CancellationToken>())
            .Returns((Team)null);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => ResolveDelete("team"));

        Assert.Equal("You do not have an active team in this View", ex.Message);
    }

    // ---- The uploaded file's name ----

    // IFormFile.Name is the multipart field name; FileName is what the client called the file. The two
    // coincide for our own UI, which posts the file under its own name as the field name, so only a
    // different client notices - and it would have every ISO stored under the field name.
    [Fact]
    public async Task Upload_NamesTheIsoAfterTheUploadedFile_NotTheFormFieldName()
    {
        var isoService = Substitute.For<IIsoService>();
        isoService.SanitizeFilename(Arg.Any<string>()).Returns(call => call.Arg<string>());
        isoService.ResolveUploadScopeIdsAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([ViewId.ToString()]);

        var file = Substitute.For<IFormFile>();
        file.Name.Returns("file");
        file.FileName.Returns("ubuntu.iso");

        var xApiService = Substitute.For<IXApiService>();
        xApiService.TrackIsoUploadedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var handler = new UploadIso(
            isoService,
            new IsoUploadOptions { MaxFileSize = 1024 },
            xApiService);

        await handler.HandleAsync(ViewId, file, "view", 1, [], CancellationToken.None);

        await isoService.Received().UploadAsync(
            ViewId, Arg.Any<IReadOnlyList<string>>(), "ubuntu.iso",
            Arg.Any<Func<System.IO.Stream>>(), Arg.Any<CancellationToken>());
    }
}
