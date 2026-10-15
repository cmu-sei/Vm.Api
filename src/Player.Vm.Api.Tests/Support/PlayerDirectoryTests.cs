// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Player.Api.Client;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// <see cref="PlayerDirectory"/> against player.api's own source, one rule per test: every denial test in
/// the suite trusts the double to refuse what player.api refuses and to show what player.api shows, so a
/// double more permissive than the sibling would let those tests pass for the wrong reason. Each summary
/// names the player.api file and lines it mirrors (paths from the player.api repo root, at commit
/// <c>e1f79ab</c> of its <c>main</c>).
/// </summary>
/// <remarks>
/// The client is the substituted <see cref="IPlayerApiClient"/> the directory answers, asked as
/// <see cref="PlayerHarness.CallerId"/>, which is what vm.api's own <c>PlayerService</c> sees on the wire.
/// </remarks>
public class PlayerDirectoryTests
{
    private readonly PlayerHarness _player = new(Guid.NewGuid(), TestContext.Current.CancellationToken);
    private readonly Guid _viewId = Guid.NewGuid();
    private readonly Guid _primary = Guid.NewGuid();
    private readonly Guid _scoped = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IPlayerApiClient Client => _player.Client;

    #region GetMyPermissions, GetMyTeamPermissions

    /// <summary>
    /// <c>Player.Api/Features/Permissions/Requests/GetMine.cs:46-50</c>: open to every caller, answering its
    /// own system permissions.
    /// </summary>
    [Fact]
    public async Task GetMyPermissions_answers_the_callers_own_system_permissions()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppSystemPermission.ViewVms));
        await _player.Actor().Holding(AppSystemPermission.ManageViews).SeedAsync();

        Assert.Equal(["ViewVms"], await Client.GetMyPermissionsAsync(Ct));
    }

    /// <summary>
    /// <c>Player.Api/Features/TeamPermissions/Requests/GetMine.cs:58-62</c>: the caller's claims, narrowed to
    /// the View asked for.
    /// </summary>
    [Fact]
    public async Task GetMyTeamPermissions_for_a_view_answers_only_the_claims_on_its_teams()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppTeamPermission.ViewTeam, _primary).Holding(AppTeamPermission.ViewTeam));

        var claims = await Client.GetMyTeamPermissionsAsync(_viewId, null, true, Ct);

        Assert.Equal([_primary], claims.Select(x => x.TeamId));
    }

    #endregion

    #region GetUserViewTeams: the caller's own visibility

    /// <summary>
    /// <c>Player.Api/Features/Teams/Requests/GetByUserView.cs:99-105</c> over
    /// <c>Player.Api/Infrastructure/Authorization/AuthorizationService.cs:173-184</c>: <c>ViewView</c> granted
    /// by the primary team's own role shows every team of the View.
    /// </summary>
    [Fact]
    public async Task GetUserViewTeams_shows_every_team_to_a_caller_whose_primary_team_grants_ViewView()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.PrimaryOn(_primary).Holding(AppViewPermission.ViewView, _viewId));

        var teams = await MyTeams();

        Assert.Equal(new[] { _primary, _scoped, _other }.Order(), teams.Select(x => x.Id).Order());
    }

    /// <summary>
    /// <c>AuthorizationService.cs:187-195</c>: <c>ViewTeam</c> on the primary team shows it and the teams
    /// scoped onto it, and no other.
    /// </summary>
    [Fact]
    public async Task GetUserViewTeams_shows_a_caller_whose_primary_team_grants_ViewTeam_that_team_and_the_teams_scoped_onto_it()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.PrimaryOn(_primary).Holding(AppTeamPermission.ViewTeam, _primary).SeesScoped(_scoped));

        var teams = await MyTeams();

        Assert.Equal(new[] { _primary, _scoped }.Order(), teams.Select(x => x.Id).Order());
    }

    /// <summary>
    /// <c>AuthorizationService.cs:187</c>: a primary team granting neither shows that team alone, even with
    /// a team scoped onto it.
    /// </summary>
    [Fact]
    public async Task GetUserViewTeams_shows_a_caller_whose_primary_team_grants_neither_only_that_team()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.PrimaryOn(_primary).Holding(AppTeamPermission.ViewTeamVms, _primary).SeesScoped(_scoped));

        Assert.Equal([_primary], (await MyTeams()).Select(x => x.Id));
    }

    /// <summary>
    /// <c>AuthorizationService.cs:173-175</c> and <c>:189-190</c> read the primary claim's direct
    /// permissions only: <c>ViewView</c> that reached the caller on a scoped claim widens nothing.
    /// </summary>
    [Fact]
    public async Task GetUserViewTeams_is_not_widened_by_ViewView_that_arrives_on_a_scoped_claim()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.PrimaryOn(_primary));
        var scoped = _player.Directory.Caller(_player.CallerId).ClaimOn(_scoped);
        scoped.Sources.Add(_primary);
        scoped.Permissions.Add(nameof(AppViewPermission.ViewView));

        Assert.Equal([_primary], (await MyTeams()).Select(x => x.Id));
    }

    /// <summary>
    /// <c>AuthorizationService.cs:168-171</c>: no primary team in the View is no team at all, even with a
    /// claim there carrying <c>ViewView</c>.
    /// </summary>
    [Fact]
    public async Task GetUserViewTeams_shows_a_caller_with_a_claim_but_no_primary_team_in_the_view_nothing()
    {
        TheViewHasThreeTeams();
        _player.Directory.Caller(_player.CallerId).ClaimOn(_other).Grant(nameof(AppViewPermission.ViewView));

        Assert.Empty(await MyTeams());
    }

    /// <summary>
    /// <c>Player.Api/Features/Teams/TeamProfile.cs:36-38</c> (<c>TeamMemberResolver</c>,
    /// <c>PrimaryTeamResolver</c>): every listed team carries the caller's own membership flags.
    /// </summary>
    [Fact]
    public async Task GetUserViewTeams_flags_the_callers_own_primary_team_and_memberships()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.PrimaryOn(_primary).Holding(AppViewPermission.ViewView, _viewId).MemberOf(_other));

        var teams = (await MyTeams()).ToDictionary(x => x.Id);

        Assert.Equal((true, true), (teams[_primary].IsMember, teams[_primary].IsPrimary));
        Assert.Equal((true, false), (teams[_other].IsMember, teams[_other].IsPrimary));
        Assert.Equal((false, false), (teams[_scoped].IsMember, teams[_scoped].IsPrimary));
    }

    #endregion

    #region GetUserViewTeams: asking about someone else, and the 404

    /// <summary>
    /// <c>Player.Api/Features/Teams/Requests/GetByUserView.cs:77-79</c>: asking about another user takes the
    /// system <c>ViewViews</c> or <c>ViewView</c> in the View.
    /// </summary>
    [Fact]
    public async Task GetUserViewTeams_about_another_user_is_refused_to_a_caller_holding_ViewTeam_in_the_view()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.PrimaryOn(_primary).Holding(AppTeamPermission.ViewTeam, _primary));
        var someone = await _player.Actor().PrimaryOn(_other).SeedAsync();

        var refusal = await Assert.ThrowsAsync<ApiException>(
            () => Client.GetUserViewTeamsAsync(_viewId, someone.Id, Ct));

        Assert.Equal(403, refusal.StatusCode);
    }

    /// <summary><c>GetByUserView.cs:106-111</c>: to a caller holding <c>ViewView</c> there, every team of the View.</summary>
    [Fact]
    public async Task GetUserViewTeams_about_another_user_answers_every_team_to_a_caller_holding_ViewView_in_the_view()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppViewPermission.ViewView, _viewId));
        var someone = await _player.Actor().PrimaryOn(_other).SeedAsync();

        var teams = await Client.GetUserViewTeamsAsync(_viewId, someone.Id, Ct);

        Assert.Contains(teams, x => x.Id == _scoped);
        Assert.Equal(3, teams.Count(x => new[] { _primary, _scoped, _other }.Contains(x.Id)));
    }

    /// <summary>
    /// <c>GetByUserView.cs:90-95</c>: a user player.api does not have is a 404 to a caller allowed to ask
    /// about others. The caller itself always exists: player.api creates its user on its first request
    /// (<c>Player.Api/Services/UserClaimsService.cs:139-164</c>).
    /// </summary>
    [Fact]
    public async Task GetUserViewTeams_about_a_user_player_api_does_not_have_is_404()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppViewPermission.ViewView, _viewId));

        var refusal = await Assert.ThrowsAsync<ApiException>(
            () => Client.GetUserViewTeamsAsync(_viewId, Guid.NewGuid(), Ct));

        Assert.Equal(404, refusal.StatusCode);
    }

    /// <summary><c>GetByUserView.cs:83-88</c>: a View player.api does not have is a 404 to a caller asking about itself.</summary>
    [Fact]
    public async Task GetUserViewTeams_for_a_view_player_api_does_not_have_is_404()
    {
        TheViewHasThreeTeams();
        var refusal = await Assert.ThrowsAsync<ApiException>(
            () => Client.GetUserViewTeamsAsync(Guid.NewGuid(), _player.CallerId, Ct));

        Assert.Equal(404, refusal.StatusCode);
    }

    #endregion

    #region GetViewTeams, the privileged listing

    /// <summary>
    /// <c>Player.Api/Features/Teams/Requests/GetByView.cs:55-56</c> over
    /// <c>Player.Api/Infrastructure/Authorization/TeamPermissionRequirement.cs:57-63</c>: <c>ViewView</c> on
    /// a claim in the View is enough.
    /// </summary>
    [Fact]
    public async Task GetViewTeams_answers_a_caller_holding_ViewView_in_the_view()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppViewPermission.ViewView, _viewId));

        var teams = await Client.GetViewTeamsAsync(_viewId, Ct);

        Assert.Contains(teams, x => x.Id == _other);
    }

    /// <summary><c>GetByView.cs:55-56</c>: <c>ViewView</c> in another View is not.</summary>
    [Fact]
    public async Task GetViewTeams_is_refused_to_a_caller_holding_ViewView_only_in_another_view()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppViewPermission.ViewView).PrimaryOn(_primary));

        var refusal = await Assert.ThrowsAsync<ApiException>(() => Client.GetViewTeamsAsync(_viewId, Ct));

        Assert.Equal(403, refusal.StatusCode);
    }

    /// <summary>
    /// <c>Player.Api/Infrastructure/Endpoints/BaseHandler.cs:16-19</c> authorizes before
    /// <c>GetByView.cs:60-65</c> looks the View up: an unknown View is a 403 to an unprivileged caller.
    /// </summary>
    [Fact]
    public async Task GetViewTeams_refuses_an_unprivileged_caller_before_it_looks_the_view_up()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.PrimaryOn(_primary));

        var refusal = await Assert.ThrowsAsync<ApiException>(() => Client.GetViewTeamsAsync(Guid.NewGuid(), Ct));

        Assert.Equal(403, refusal.StatusCode);
    }

    /// <summary><c>GetByView.cs:60-65</c>: the system <c>ViewViews</c> gets the 404 for an unknown View.</summary>
    [Fact]
    public async Task GetViewTeams_for_a_view_player_api_does_not_have_is_404_to_a_caller_holding_ViewViews()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppSystemPermission.ViewViews));

        var refusal = await Assert.ThrowsAsync<ApiException>(() => Client.GetViewTeamsAsync(Guid.NewGuid(), Ct));

        Assert.Equal(404, refusal.StatusCode);
    }

    #endregion

    #region GetTeam

    /// <summary>
    /// <c>Player.Api/Features/Teams/Requests/Get.cs:53-54</c> over <c>TeamPermissionRequirement.cs:47-53</c>:
    /// <c>ViewTeam</c> on the caller's claim on that team.
    /// </summary>
    [Fact]
    public async Task GetTeam_answers_a_caller_holding_ViewTeam_on_the_team()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppTeamPermission.ViewTeam, _other));

        Assert.Equal(_viewId, (await Client.GetTeamAsync(_other, Ct)).ViewId);
    }

    /// <summary><c>Get.cs:53-54</c> over <c>TeamPermissionRequirement.cs:57-63</c>: or <c>ViewView</c> on a claim in its View.</summary>
    [Fact]
    public async Task GetTeam_answers_a_caller_holding_ViewView_in_the_teams_view()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppViewPermission.ViewView, _viewId));

        Assert.Equal(_other, (await Client.GetTeamAsync(_other, Ct)).Id);
    }

    /// <summary><c>Get.cs:53-54</c>: a Map or Vm permission on the team is neither.</summary>
    [Fact]
    public async Task GetTeam_is_refused_to_a_caller_holding_ManageTeamMaps_on_the_team()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppTeamPermission.ManageTeamMaps, _other));

        var refusal = await Assert.ThrowsAsync<ApiException>(() => Client.GetTeamAsync(_other, Ct));

        Assert.Equal(403, refusal.StatusCode);
    }

    /// <summary>
    /// <c>Player.Api/Infrastructure/Authorization/AuthorizationService.cs:86-93</c>: a team player.api does
    /// not have fails the authorization, so an unprivileged caller is refused rather than told it is missing.
    /// </summary>
    [Fact]
    public async Task GetTeam_refuses_an_unprivileged_caller_a_team_player_api_does_not_have()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppViewPermission.ViewView, _viewId));

        var refusal = await Assert.ThrowsAsync<ApiException>(() => Client.GetTeamAsync(Guid.NewGuid(), Ct));

        Assert.Equal(403, refusal.StatusCode);
    }

    /// <summary><c>Get.cs:58-63</c>: the system <c>ViewViews</c> gets the 404.</summary>
    [Fact]
    public async Task GetTeam_for_a_team_player_api_does_not_have_is_404_to_a_caller_holding_ViewViews()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppSystemPermission.ViewViews));

        var refusal = await Assert.ThrowsAsync<ApiException>(() => Client.GetTeamAsync(Guid.NewGuid(), Ct));

        Assert.Equal(404, refusal.StatusCode);
    }

    #endregion

    #region GetView, GetViews

    /// <summary>
    /// <c>Player.Api/Features/Views/Requests/Get.cs:53-55</c> over <c>AuthorizationService.cs:120-126</c>:
    /// any claim on a team of the View, whatever it carries.
    /// </summary>
    [Fact]
    public async Task GetView_answers_a_member_of_one_of_its_teams_holding_nothing()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.MemberOf(_other));

        Assert.Equal(_viewId, (await Client.GetViewAsync(_viewId, Ct)).Id);
    }

    /// <summary><c>Views/Requests/Get.cs:53-55</c>: a caller with no claim there and no <c>ViewViews</c> is refused.</summary>
    [Fact]
    public async Task GetView_is_refused_to_a_caller_holding_ViewView_only_in_another_view()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppViewPermission.ViewView));

        var refusal = await Assert.ThrowsAsync<ApiException>(() => Client.GetViewAsync(_viewId, Ct));

        Assert.Equal(403, refusal.StatusCode);
    }

    /// <summary><c>Views/Requests/Get.cs:59-63</c>: the system <c>ViewViews</c> gets the 404 for an unknown View.</summary>
    [Fact]
    public async Task GetView_for_a_view_player_api_does_not_have_is_404_to_a_caller_holding_ViewViews()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppSystemPermission.ViewViews));

        var refusal = await Assert.ThrowsAsync<ApiException>(() => Client.GetViewAsync(Guid.NewGuid(), Ct));

        Assert.Equal(404, refusal.StatusCode);
    }

    /// <summary><c>Player.Api/Features/Views/Requests/GetAll.cs:49-50</c>: the system <c>ViewViews</c>, and nothing else.</summary>
    [Fact]
    public async Task GetViews_is_refused_to_a_caller_holding_ManageViews_without_ViewViews()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppSystemPermission.ManageViews).Holding(AppViewPermission.ViewView, _viewId));

        var refusal = await Assert.ThrowsAsync<ApiException>(() => Client.GetViewsAsync(Ct));

        Assert.Equal(403, refusal.StatusCode);
    }

    /// <summary><c>GetAll.cs:49-50</c>: to <c>ViewViews</c>, every View.</summary>
    [Fact]
    public async Task GetViews_answers_every_view_to_a_caller_holding_ViewViews()
    {
        TheViewHasThreeTeams();
        _player.Grant(b => b.Holding(AppSystemPermission.ViewViews));

        Assert.Contains(await Client.GetViewsAsync(Ct), x => x.Id == _viewId);
    }

    #endregion

    /// <summary>The three teams of the View every test asks about.</summary>
    private void TheViewHasThreeTeams()
    {
        _player.Directory.PlaceTeam(_primary, _viewId, "primary");
        _player.Directory.PlaceTeam(_scoped, _viewId, "scoped");
        _player.Directory.PlaceTeam(_other, _viewId, "other");
    }

    private Task<System.Collections.Generic.ICollection<Team>> MyTeams() =>
        Client.GetUserViewTeamsAsync(_viewId, _player.CallerId, Ct);
}
