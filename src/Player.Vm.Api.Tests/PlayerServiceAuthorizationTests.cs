// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Authorization;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// PlayerService is where every authorization decision in this application ends up: VmService,
/// NetworkService, IsoService and every Proxmox and vSphere handler ask it, and it answers by
/// interpreting claims fetched from player.api. Nothing above it re-checks its verdict, so a wrong
/// answer here is a privilege escalation rather than a bug in one endpoint.
///
/// These tests substitute the player.api client and drive the interpretation directly. Two of its
/// rules are deliberate and easy to "simplify" into a security hole, and both have a test that fails
/// if that happens: a permission scoped onto the caller's team from elsewhere must not authorize an
/// operation on an unrelated team, and visibility is decided from the primary team's *direct*
/// permissions only. Both mirror player.api's own AuthorizationService; the comments in
/// PlayerService.Can and GetVisibilityContextAsync say so.
/// </summary>
public class PlayerServiceAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IPlayerApiClient _client = Substitute.For<IPlayerApiClient>();
    private readonly IViewService _viewService = Substitute.For<IViewService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly PlayerService _service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PlayerServiceAuthorizationTests()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", UserId.ToString())]));
        accessor.HttpContext.Returns(context);

        // No system permissions unless a test grants them. Left unstubbed, the substitute returns
        // null, which is a different code path - tested separately below.
        SystemPermissions();

        _service = new PlayerService(accessor, _client, _viewService, _cache);
    }

    #region Can - system permissions

    /// <summary>
    /// A system permission short-circuits before any team or view lookup. Asserting that nothing was
    /// asked of IViewService is what pins the short-circuit rather than just the answer.
    /// </summary>
    [Fact]
    public async Task Can_GrantedBySystemPermission_AsksNothingAboutTeams()
    {
        SystemPermissions(nameof(AppSystemPermission.ViewViews));

        var allowed = await _service.Can(
            [Guid.NewGuid()], null, [AppSystemPermission.ViewViews], [], [], Ct);

        Assert.True(allowed);
        await _viewService.DidNotReceive().GetViewIdForTeam(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // player.api is free to add permissions this build has never heard of. An unparseable value has to
    // be ignored, not throw, and must not be mistaken for a permission that was required.
    [Fact]
    public async Task Can_IgnoresAPermissionValueItCannotParse()
    {
        SystemPermissions("APermissionFromANewerPlayerApi");

        Assert.False(await _service.Can(null, null, [AppSystemPermission.ViewViews], [], [], Ct));
    }

    // A null response from player.api must read as "no permissions", not throw.
    [Fact]
    public async Task Can_TreatsANullPermissionResponseAsNone()
    {
        _client.GetMyPermissionsAsync(Arg.Any<CancellationToken>()).Returns((ICollection<string>)null);

        Assert.False(await _service.Can(null, null, [AppSystemPermission.ViewViews], [], [], Ct));
    }

    /// <summary>
    /// Fail closed. Every caller passes literal permission arrays, so an empty one means "no way to
    /// satisfy this", and the only safe answer is no. Returning true would silently open any endpoint
    /// whose required-permission list was left empty by mistake.
    /// </summary>
    [Fact]
    public async Task Can_DeniesWhenNoPermissionWouldSatisfyIt()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        TeamInView(teamId, viewId);
        TeamPermissions(viewId, TeamClaim(teamId, isPrimary: true, direct: nameof(AppViewPermission.ManageView)));

        Assert.False(await _service.Can([teamId], null, [], [], [], Ct));
    }

    // Guid.Empty is what an unset team id deserializes to. It is dropped before any lookup, so an
    // empty id cannot become a view lookup or a permission match.
    [Fact]
    public async Task Can_DropsAnEmptyTeamId()
    {
        Assert.False(await _service.Can(
            [Guid.Empty], null, [], [AppViewPermission.ViewView], [], Ct));

        await _viewService.DidNotReceive().GetViewIdForTeam(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Can - view permissions

    // The ordinary grant: the permission is held on the team being operated on.
    [Fact]
    public async Task Can_GrantsAViewPermissionHeldOnTheRequestedTeam()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        TeamInView(teamId, viewId);
        TeamPermissions(viewId, TeamClaim(teamId, direct: nameof(AppViewPermission.ManageView)));

        Assert.True(await _service.Can(
            [teamId], null, [], [AppViewPermission.ManageView], [], Ct));
    }

    /// <summary>
    /// The fallback: a view-level permission held on the caller's own team authorizes work on another
    /// team in the same view. That is what makes a view admin a view admin.
    /// </summary>
    [Fact]
    public async Task Can_FallsBackToAViewPermissionHeldOnTheCallersOwnTeam()
    {
        var viewId = Guid.NewGuid();
        var ownTeam = Guid.NewGuid();
        var otherTeam = Guid.NewGuid();

        TeamInView(otherTeam, viewId);
        UserViewTeams(viewId, ViewTeam(ownTeam, isMember: true), ViewTeam(otherTeam));
        TeamPermissions(
            viewId,
            TeamClaim(ownTeam, isPrimary: true, direct: nameof(AppViewPermission.ManageView)),
            TeamClaim(otherTeam));

        Assert.True(await _service.Can(
            [otherTeam], null, [], [AppViewPermission.ManageView], [], Ct));
    }

    /// <summary>
    /// The rule this whole class exists for. A claim can appear in the View because some *other* team
    /// scoped its permissions onto the caller's team; that claim's own permissions must not authorize
    /// an operation on the team it belongs to. Checking "any claim in the View has ManageView" instead
    /// of the requested and direct teams would pass this test's setup and hand the caller a team they
    /// have no relationship with.
    /// </summary>
    [Fact]
    public async Task Can_DoesNotLetAScopedInClaimAuthorizeAnUnrelatedTeam()
    {
        var viewId = Guid.NewGuid();
        var ownTeam = Guid.NewGuid();
        var scopedTeam = Guid.NewGuid();
        var targetTeam = Guid.NewGuid();

        TeamInView(targetTeam, viewId);

        // The caller belongs to ownTeam only. scopedTeam and targetTeam are not theirs.
        UserViewTeams(viewId, ViewTeam(ownTeam, isMember: true, isPrimary: true), ViewTeam(scopedTeam), ViewTeam(targetTeam));

        TeamPermissions(
            viewId,
            // The caller's own team: visible, but holds nothing that would authorize this.
            TeamClaim(ownTeam, isPrimary: true, direct: nameof(AppViewPermission.ViewView)),
            // Present only because scopedTeam granted its permissions onto ownTeam.
            TeamClaim(scopedTeam, direct: nameof(AppViewPermission.ManageView), sourceTeamIds: [ownTeam]),
            TeamClaim(targetTeam));

        Assert.False(await _service.Can(
            [targetTeam], null, [], [AppViewPermission.ManageView], [], Ct));
    }

    // A view id can be requested directly, with no team involved - NetworkService does exactly this.
    [Fact]
    public async Task Can_GrantsAViewPermissionForADirectlyRequestedView()
    {
        var viewId = Guid.NewGuid();
        var ownTeam = Guid.NewGuid();

        UserViewTeams(viewId, ViewTeam(ownTeam, isMember: true));
        TeamPermissions(viewId, TeamClaim(ownTeam, isPrimary: true, direct: nameof(AppViewPermission.ManageNetworks)));

        Assert.True(await _service.Can(
            [], [viewId], [], [AppViewPermission.ManageNetworks], [], Ct));
    }

    #endregion

    #region Can - team permissions

    [Fact]
    public async Task Can_GrantsATeamPermissionHeldOnTheRequestedTeam()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        TeamInView(teamId, viewId);
        TeamPermissions(viewId, TeamClaim(teamId, direct: nameof(AppTeamPermission.ManageTeam)));

        Assert.True(await _service.Can(
            [teamId], null, [], [], [AppTeamPermission.ManageTeam], Ct));
    }

    /// <summary>
    /// Team permissions have no equivalent of the direct-team fallback that view permissions get:
    /// ManageTeam on your own team says nothing about anyone else's. Contrast
    /// <see cref="Can_FallsBackToAViewPermissionHeldOnTheCallersOwnTeam"/>, where the same shape
    /// succeeds because the permission is view-scoped. The asymmetry reads like an oversight and is
    /// not one - a team permission is by definition about one team.
    /// </summary>
    [Fact]
    public async Task Can_DoesNotExtendATeamPermissionToAnotherTeam()
    {
        var viewId = Guid.NewGuid();
        var ownTeam = Guid.NewGuid();
        var otherTeam = Guid.NewGuid();

        TeamInView(otherTeam, viewId);
        UserViewTeams(viewId, ViewTeam(ownTeam, isMember: true), ViewTeam(otherTeam));
        TeamPermissions(
            viewId,
            TeamClaim(ownTeam, isPrimary: true, direct: nameof(AppTeamPermission.ManageTeam)),
            TeamClaim(otherTeam));

        Assert.False(await _service.Can(
            [otherTeam], null, [], [], [AppTeamPermission.ManageTeam], Ct));
    }

    // CanViewTeams is the wrapper CanAccessVm uses, and it accepts any of six permissions. This pins
    // the mapping rather than the underlying Can, which the tests above cover.
    [Fact]
    public async Task CanViewTeams_AcceptsATeamLevelViewPermission()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        TeamInView(teamId, viewId);
        TeamPermissions(viewId, TeamClaim(teamId, direct: nameof(AppTeamPermission.ViewTeam)));

        Assert.True(await _service.CanViewTeams([teamId], Ct));
    }

    // Manage implies view throughout: every wrapper lists the manage permission alongside the one it
    // is named for, so an admin never has to be granted both.
    [Fact]
    public async Task CanViewTeams_AcceptsTheManagePermissionInPlaceOfView()
    {
        SystemPermissions(nameof(AppSystemPermission.ManageViews));

        Assert.True(await _service.CanViewTeams([Guid.NewGuid()], Ct));
    }

    [Fact]
    public async Task CanManageTeams_DoesNotAcceptAViewOnlyPermission()
    {
        SystemPermissions(nameof(AppSystemPermission.ViewViews));

        Assert.False(await _service.CanManageTeams([Guid.NewGuid()], Ct));
    }

    #endregion

    #region Caching

    /// <summary>
    /// System permissions are cached per user for a minute. Every request makes several Can calls, so
    /// without this each one would be a round trip to player.api.
    /// </summary>
    [Fact]
    public async Task Can_FetchesSystemPermissionsOnlyOnce()
    {
        SystemPermissions(nameof(AppSystemPermission.ViewViews));

        await _service.Can(null, null, [AppSystemPermission.ViewViews], [], [], Ct);
        await _service.Can(null, null, [AppSystemPermission.ViewViews], [], [], Ct);

        await _client.Received(1).GetMyPermissionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Can_FetchesTeamPermissionsOncePerView()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        TeamInView(teamId, viewId);
        TeamPermissions(viewId, TeamClaim(teamId, direct: nameof(AppViewPermission.ManageView)));

        await _service.Can([teamId], null, [], [AppViewPermission.ManageView], [], Ct);
        await _service.Can([teamId], null, [], [AppViewPermission.ManageView], [], Ct);

        await _client.Received(1).GetMyTeamPermissionsAsync(viewId, null, true, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A null result is not cached. Memoizing one would make a transient player.api failure sticky for
    /// the rest of the request and indistinguishable from "this user has no claims" - so the first
    /// call denies and the second, once player.api answers, allows.
    /// </summary>
    [Fact]
    public async Task Can_DoesNotCacheAFailedTeamPermissionsFetch()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        TeamInView(teamId, viewId);

        _client.GetMyTeamPermissionsAsync(viewId, null, true, Arg.Any<CancellationToken>())
            .Returns(
                _ => (ICollection<TeamPermissionsClaim>)null,
                _ => [TeamClaim(teamId, direct: nameof(AppViewPermission.ManageView))]);

        Assert.False(await _service.Can([teamId], null, [], [AppViewPermission.ManageView], [], Ct));
        Assert.True(await _service.Can([teamId], null, [], [AppViewPermission.ManageView], [], Ct));
    }

    #endregion

    #region Visibility

    // No primary team means no foothold in the View, and so nothing visible.
    [Fact]
    public async Task Visibility_WithNoPrimaryTeam_IsEmpty()
    {
        var viewId = Guid.NewGuid();
        TeamPermissions(viewId, TeamClaim(Guid.NewGuid(), direct: nameof(AppViewPermission.ManageView)));

        var visibility = await _service.GetVisibilityContextAsync(viewId, Ct);

        Assert.Null(visibility.PrimaryTeamId);
        Assert.False(visibility.CanViewAllTeams);
        Assert.Empty(visibility.TeamIds);
    }

    // A view-wide permission on the primary team sees every team in the View.
    [Fact]
    public async Task Visibility_WithDirectViewPermission_SeesEveryTeam()
    {
        var viewId = Guid.NewGuid();
        var primary = Guid.NewGuid();
        var other = Guid.NewGuid();

        UserViewTeams(viewId, ViewTeam(primary, isMember: true, isPrimary: true), ViewTeam(other));
        TeamPermissions(viewId, TeamClaim(primary, isPrimary: true, direct: nameof(AppViewPermission.ViewView)));

        var visibility = await _service.GetVisibilityContextAsync(viewId, Ct);

        Assert.True(visibility.CanViewAllTeams);
        Assert.Equal(primary, visibility.PrimaryTeamId);
        Assert.Equal<Guid>([primary, other], visibility.TeamIds.OrderBy(x => x == other).ToArray());
    }

    /// <summary>
    /// The second rule worth protecting: visibility comes from the primary team's *direct* permissions.
    /// Here ViewView is present in the effective set but was scoped in from elsewhere, and it must not
    /// widen what this team can see. Reading PermissionValues instead of DirectPermissionValues would
    /// turn any scoped grant into view-wide visibility.
    /// </summary>
    [Fact]
    public async Task Visibility_IgnoresAViewPermissionThatWasScopedIn()
    {
        var viewId = Guid.NewGuid();
        var primary = Guid.NewGuid();
        var other = Guid.NewGuid();

        UserViewTeams(viewId, ViewTeam(primary, isMember: true, isPrimary: true), ViewTeam(other));
        TeamPermissions(viewId, TeamClaim(primary, isPrimary: true, scoped: nameof(AppViewPermission.ViewView)));

        var visibility = await _service.GetVisibilityContextAsync(viewId, Ct);

        Assert.False(visibility.CanViewAllTeams);
        Assert.Equal<Guid>([primary], visibility.TeamIds.ToArray());
    }

    /// <summary>
    /// Without a view-wide permission, a team-level one sees the teams that scoped their permissions
    /// onto the primary team - and only those.
    /// </summary>
    [Fact]
    public async Task Visibility_WithDirectTeamPermission_SeesTeamsScopedOntoIt()
    {
        var viewId = Guid.NewGuid();
        var primary = Guid.NewGuid();
        var scopedOnto = Guid.NewGuid();
        var unrelated = Guid.NewGuid();

        TeamPermissions(
            viewId,
            TeamClaim(primary, isPrimary: true, direct: nameof(AppTeamPermission.ViewTeam)),
            TeamClaim(scopedOnto, sourceTeamIds: [primary]),
            TeamClaim(unrelated, sourceTeamIds: [Guid.NewGuid()]));

        var visibility = await _service.GetVisibilityContextAsync(viewId, Ct);

        Assert.False(visibility.CanViewAllTeams);
        Assert.Contains(primary, visibility.TeamIds);
        Assert.Contains(scopedOnto, visibility.TeamIds);
        Assert.DoesNotContain(unrelated, visibility.TeamIds);
    }

    // An unknown View grants no visibility rather than throwing. Callers that need to tell "not found"
    // from "nothing visible" probe the teams endpoint separately - see GetTeamsByViewIdAsync below.
    [Fact]
    public async Task Visibility_ForAnUnknownView_IsEmpty()
    {
        var viewId = Guid.NewGuid();
        _client.GetMyTeamPermissionsAsync(viewId, null, true, Arg.Any<CancellationToken>())
            .ThrowsAsync(NotFound());

        var visibility = await _service.GetVisibilityContextAsync(viewId, Ct);

        Assert.Same(VisibilityContext.Empty, visibility);
    }

    #endregion

    #region View and team lookups

    /// <summary>
    /// null, not empty. An unknown View has to reach the caller as a 404, and every caller of this
    /// method branches on null to produce one. The teams endpoint is the probe that distinguishes the
    /// two cases, which is why it is called before visibility rather than after.
    /// </summary>
    [Fact]
    public async Task GetTeamsByViewId_ForAnUnknownView_IsNull()
    {
        var viewId = Guid.NewGuid();
        _client.GetUserViewTeamsAsync(viewId, UserId, Arg.Any<CancellationToken>()).ThrowsAsync(NotFound());

        Assert.Null(await _service.GetTeamsByViewIdAsync(viewId, Ct));
    }

    [Fact]
    public async Task GetTeamsByViewId_ReturnsOnlyVisibleTeams()
    {
        var viewId = Guid.NewGuid();
        var primary = Guid.NewGuid();
        var invisible = Guid.NewGuid();

        UserViewTeams(viewId, ViewTeam(primary, isMember: true, isPrimary: true), ViewTeam(invisible));
        TeamPermissions(viewId, TeamClaim(primary, isPrimary: true, direct: nameof(AppTeamPermission.ViewTeam)));

        var teams = await _service.GetTeamsByViewIdAsync(viewId, Ct);

        Assert.Equal<Guid>([primary], teams.Select(x => x.Id).ToArray());
    }

    // A team nobody can place in a View is not visible, and must not throw on the way to that answer.
    [Fact]
    public async Task IsTeamVisible_ForATeamInNoView_IsFalse()
    {
        Assert.False(await _service.IsTeamVisibleAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task IsTeamVisible_ForATeamInTheVisibilitySet_IsTrue()
    {
        var viewId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        TeamInView(teamId, viewId);
        TeamPermissions(viewId, TeamClaim(teamId, isPrimary: true, direct: nameof(AppTeamPermission.ViewTeam)));

        Assert.True(await _service.IsTeamVisibleAsync(teamId, Ct));
    }

    /// <summary>
    /// The group id is what the SignalR hubs use to scope a broadcast. A caller who can see the whole
    /// View joins the View-wide group; anyone else joins only their primary team's, which is what keeps
    /// one team's VM updates from reaching another.
    /// </summary>
    [Fact]
    public async Task GetGroupIdForView_IsTheViewForAViewWideCallerAndTheTeamOtherwise()
    {
        var viewId = Guid.NewGuid();
        var primary = Guid.NewGuid();

        UserViewTeams(viewId, ViewTeam(primary, isMember: true, isPrimary: true));
        TeamPermissions(viewId, TeamClaim(primary, isPrimary: true, direct: nameof(AppViewPermission.ViewView)));

        Assert.Equal(viewId, await _service.GetGroupIdForViewAsync(viewId, Ct));

        var scopedViewId = Guid.NewGuid();
        var scopedPrimary = Guid.NewGuid();
        TeamPermissions(scopedViewId, TeamClaim(scopedPrimary, isPrimary: true, direct: nameof(AppTeamPermission.ViewTeam)));

        Assert.Equal(scopedPrimary, await _service.GetGroupIdForViewAsync(scopedViewId, Ct));
    }

    // No primary team, no group to join - and null rather than Guid.Empty, which would be a real group.
    [Fact]
    public async Task GetGroupIdForView_WithNoPrimaryTeam_IsNull()
    {
        var viewId = Guid.NewGuid();
        TeamPermissions(viewId);

        Assert.Null(await _service.GetGroupIdForViewAsync(viewId, Ct));
    }

    #endregion

    #region Helpers

    private void SystemPermissions(params string[] permissions) =>
        _client.GetMyPermissionsAsync(Arg.Any<CancellationToken>()).Returns(permissions.ToList());

    // Matches production's call arguments exactly, so the stub is hit for the right View.
    private void TeamPermissions(Guid viewId, params TeamPermissionsClaim[] claims) =>
        _client.GetMyTeamPermissionsAsync(viewId, null, true, Arg.Any<CancellationToken>())
            .Returns(claims.ToList());

    private void UserViewTeams(Guid viewId, params Team[] teams) =>
        _client.GetUserViewTeamsAsync(viewId, UserId, Arg.Any<CancellationToken>()).Returns(teams.ToList());

    private void TeamInView(Guid teamId, Guid viewId) =>
        _viewService.GetViewIdForTeam(teamId, Arg.Any<CancellationToken>()).Returns(viewId);

    /// <param name="direct">
    /// Permissions granted to this team directly. Also effective, so they land in both collections -
    /// which is what makes <paramref name="scoped"/> the interesting case.
    /// </param>
    /// <param name="scoped">
    /// Permissions that are effective but were granted from another team, so they appear in
    /// PermissionValues and not DirectPermissionValues.
    /// </param>
    private static TeamPermissionsClaim TeamClaim(
        Guid teamId,
        bool isPrimary = false,
        string direct = null,
        string scoped = null,
        Guid[] sourceTeamIds = null)
    {
        List<string> directValues = direct is null ? [] : [direct];
        List<string> effective = scoped is null ? [.. directValues] : [.. directValues, scoped];

        return new TeamPermissionsClaim
        {
            TeamId = teamId,
            IsPrimary = isPrimary,
            DirectPermissionValues = directValues,
            PermissionValues = effective,
            SourceTeamIds = sourceTeamIds ?? []
        };
    }

    private static Team ViewTeam(Guid id, bool isMember = false, bool isPrimary = false) =>
        new() { Id = id, Name = $"team-{id}", IsMember = isMember, IsPrimary = isPrimary };

    private static ApiException NotFound() =>
        new("View not found", 404, null, null, null);

    #endregion
}
