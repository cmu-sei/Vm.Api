// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api derives permissions in PlayerService from what player.api answers, so each actor shape is checked
// through the real PlayerService over the player.api data the builder wrote: the step's permission passes,
// its near misses (the neighbouring permission, the same permission on another team or View) do not. The
// directory's own fidelity to player.api is pinned here too: the primary team, the caller's View teams as
// its primary visibility context, the gated privileged listings and what a scoped claim carries. No
// request: a status code cannot tell which step granted it.

using System;
using System.Linq;
using System.Threading.Tasks;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Tests.Support;

/// <summary>Tests for <see cref="TestActorBuilder"/>: an actor that holds more than asked turns an authorization test into a formality.</summary>
public class TestActorTests
{
    private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PlayerHarness _player = new(Guid.NewGuid(), Ct);

    [Fact]
    public async Task WithId_and_WithName_fix_the_identity()
    {
        var id = Guid.NewGuid();

        var actor = await _player.Actor().WithId(id).WithName("Named Actor").SeedAsync();

        Assert.Equal((id, "Named Actor"), (actor.Id, actor.Name));
    }

    [Fact]
    public async Task Two_actors_are_two_identities()
    {
        var first = await _player.Actor().SeedAsync();
        var second = await _player.Actor().SeedAsync();

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void WithName_refuses_an_empty_name()
    {
        Assert.Throws<ArgumentException>(() => _player.Actor().WithName(" "));
    }

    [Fact]
    public async Task An_actor_with_no_steps_holds_nothing()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        await _player.Actor().InView(teamId, viewId).SeedAsync();

        await _player.Caller().SeedAsync();
        var service = _player.Service;

        Assert.Empty(await service.GetSystemPermissionsAsync(Ct));
        Assert.False(await service.CanViewVms([teamId], [viewId], Ct));
    }

    [Fact]
    public async Task Holding_a_system_permission_grants_exactly_it_everywhere()
    {
        await _player.Caller().Holding(AppSystemPermission.ViewVms).SeedAsync();
        var service = _player.Service;

        Assert.Equal([AppSystemPermission.ViewVms], await service.GetSystemPermissionsAsync(Ct));
        Assert.Equal(
            (true, true, false),
            (await service.CanViewVms([], [], Ct),
                await service.CanViewVms([Guid.NewGuid()], null, Ct),
                await service.CanControlVms([Guid.NewGuid()], Ct)));
    }

    [Fact]
    public async Task Holding_a_team_permission_grants_it_on_that_team_only()
    {
        var (mine, other) = (Guid.NewGuid(), Guid.NewGuid());
        await _player.Actor().InView(other, Guid.NewGuid()).SeedAsync();

        await _player.Caller().Holding(AppTeamPermission.ManageTeam, mine).SeedAsync();
        var service = _player.Service;

        Assert.Equal(
            (true, false, false),
            (await service.CanManageTeams([mine], Ct),
                await service.CanManageTeams([other], Ct),
                await service.CanControlVms([mine], Ct)));
    }

    [Fact]
    public async Task Holding_a_view_permission_grants_it_for_the_view_and_its_teams_only()
    {
        var (view, otherView, team, teamInOtherView) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await _player.Caller()
            .InView(team, view)
            .InView(teamInOtherView, otherView)
            .Holding(AppViewPermission.ControlViewVms, view)
            .SeedAsync();
        var service = _player.Service;

        Assert.Equal(
            (true, true, false, false),
            (await service.CanControlVms([team], Ct),
                await service.Can([], [view], [], [AppViewPermission.ControlViewVms], [], Ct),
                await service.CanControlVms([teamInOtherView], Ct),
                await service.Can([], [view], [], [AppViewPermission.ViewViewVms], [], Ct)));
    }

    [Fact]
    public async Task Holding_a_view_permission_puts_it_on_the_actors_primary_team_in_the_view()
    {
        var (view, primary) = (Guid.NewGuid(), Guid.NewGuid());

        await _player.Caller().InView(primary, view).PrimaryOn(primary).Holding(AppViewPermission.ViewView, view).SeedAsync();

        var claim = Assert.Single(_player.Directory.Caller(_player.CallerId).Claims);
        Assert.Equal((primary, true), (claim.Key, claim.Value.Primary));
        Assert.Equal([nameof(AppViewPermission.ViewView)], claim.Value.Direct);
    }

    [Fact]
    public async Task PrimaryOn_alone_sees_the_primary_team_only()
    {
        var (view, primary, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _player.Actor().InView(other, view).SeedAsync();

        await _player.Caller().InView(primary, view).PrimaryOn(primary).SeedAsync();
        var visibility = await _player.Service.GetVisibilityContextAsync(view, Ct);

        Assert.Equal((primary, false), (visibility.PrimaryTeamId, visibility.CanViewAllTeams));
        Assert.Equal([primary], visibility.TeamIds);
    }

    [Fact]
    public async Task PrimaryOn_with_ViewView_sees_every_team_of_the_view()
    {
        var (view, primary, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _player.Actor().InView(other, view).SeedAsync();

        await _player.Caller().InView(primary, view).PrimaryOn(primary).Holding(AppViewPermission.ViewView, view).SeedAsync();
        var visibility = await _player.Service.GetVisibilityContextAsync(view, Ct);

        Assert.True(visibility.CanViewAllTeams);
        Assert.Equal(new[] { primary, other }.Order(), visibility.TeamIds.Order());
    }

    [Fact]
    public async Task SeesScoped_with_ViewTeam_on_the_primary_sees_the_scoped_team_and_no_other()
    {
        var (view, primary, scoped, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _player.Actor().InView(other, view).SeedAsync();

        await _player.Caller()
            .InView(primary, view).InView(scoped, view)
            .PrimaryOn(primary).Holding(AppTeamPermission.ViewTeam, primary)
            .SeesScoped(scoped)
            .SeedAsync();
        var visibility = await _player.Service.GetVisibilityContextAsync(view, Ct);

        Assert.Equal(new[] { primary, scoped }.Order(), visibility.TeamIds.Order());
    }

    [Fact]
    public async Task SeesScoped_without_a_primary_team_throws()
    {
        var builder = _player.Caller().InView(Guid.NewGuid(), Guid.NewGuid()).SeesScoped(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(builder.SeedAsync);
    }

    [Fact]
    public async Task The_first_membership_in_a_view_is_the_primary_team()
    {
        var (view, first, second) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await _player.Caller()
            .InView(first, view).InView(second, view)
            .Holding(AppTeamPermission.ControlTeamVms, first)
            .Holding(AppTeamPermission.ViewTeamVms, second)
            .SeedAsync();

        Assert.Equal(first, (await _player.Service.GetPrimaryTeamByViewIdAsync(view, Ct)).Id);
    }

    [Fact]
    public async Task PrimaryOn_names_the_primary_team_in_place_of_the_first_membership()
    {
        var (view, first, named) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await _player.Caller()
            .InView(first, view).InView(named, view)
            .Holding(AppTeamPermission.ControlTeamVms, first)
            .PrimaryOn(named)
            .SeedAsync();

        Assert.Equal(named, (await _player.Service.GetPrimaryTeamByViewIdAsync(view, Ct)).Id);
    }

    /// <summary>
    /// The caller's View teams are its primary visibility context, not every team it is on: a second
    /// membership that the primary team's permissions do not reach is not among them.
    /// </summary>
    [Fact]
    public async Task The_callers_view_teams_are_its_primary_visibility_context()
    {
        var (view, primary, alsoOn) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await _player.Caller()
            .InView(primary, view).InView(alsoOn, view)
            .Holding(AppTeamPermission.ViewTeamVms, primary)
            .MemberOf(alsoOn)
            .SeedAsync();

        Assert.Equal([primary], (await _player.Service.GetTeamsByViewIdAsync(view, Ct)).Select(x => x.Id));
    }

    [Fact]
    public async Task The_all_teams_listing_answers_a_caller_holding_ViewView_on_the_view()
    {
        var (view, team, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _player.Actor().InView(other, view).SeedAsync();

        await _player.Caller().InView(team, view).MemberOf(team).Holding(AppViewPermission.ViewView, view).SeedAsync();

        Assert.Equal(new[] { team, other }.Order(), (await _player.Service.GetAllTeamsByViewIdAsync(view, Ct)).Select(x => x.Id).Order());
    }

    [Fact]
    public async Task The_all_teams_listing_refuses_a_caller_holding_ViewView_only_on_another_view()
    {
        var (view, otherView) = (Guid.NewGuid(), Guid.NewGuid());
        await _player.Actor().InView(Guid.NewGuid(), view).SeedAsync();

        await _player.Caller().Holding(AppViewPermission.ViewView, otherView).SeedAsync();

        var refusal = await Assert.ThrowsAsync<Player.Api.Client.ApiException>(() => _player.Service.GetAllTeamsByViewIdAsync(view, Ct));
        Assert.Equal(403, refusal.StatusCode);
    }

    [Fact]
    public async Task Every_view_is_listed_for_a_caller_holding_ViewViews()
    {
        var view = Guid.NewGuid();
        _player.Directory.AddView(view);

        await _player.Caller().Holding(AppSystemPermission.ViewViews).SeedAsync();

        Assert.Contains(view, (await _player.Service.GetAllViewsAsync(Ct)).Select(x => x.Id));
    }

    [Fact]
    public async Task Every_view_is_refused_to_a_caller_holding_only_ViewView_on_a_view()
    {
        await _player.Caller().Holding(AppViewPermission.ViewView, Guid.NewGuid()).SeedAsync();

        var refusal = await Assert.ThrowsAsync<Player.Api.Client.ApiException>(() => _player.Service.GetAllViewsAsync(Ct));
        Assert.Equal(403, refusal.StatusCode);
    }

    [Fact]
    public async Task A_scoped_claim_carries_the_permissions_of_the_membership_granting_it()
    {
        var (view, primary, scoped) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await _player.Caller()
            .InView(primary, view).InView(scoped, view)
            .PrimaryOn(primary).Holding(AppTeamPermission.ControlTeamVms, primary)
            .SeesScoped(scoped)
            .SeedAsync();

        Assert.True(await _player.Service.CanControlVms([scoped], Ct));
    }

    [Fact]
    public async Task A_scoped_claim_carries_no_more_than_the_granting_membership()
    {
        var (view, primary, scoped) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await _player.Caller()
            .InView(primary, view).InView(scoped, view)
            .PrimaryOn(primary).Holding(AppTeamPermission.ViewTeamVms, primary)
            .SeesScoped(scoped)
            .SeedAsync();
        var service = _player.Service;

        Assert.Equal((true, false), (await service.CanViewVms([scoped], null, Ct), await service.CanControlVms([scoped], Ct)));
    }

    [Fact]
    public async Task One_actors_grants_do_not_answer_for_another()
    {
        var holder = await _player.Actor().Holding(AppSystemPermission.ViewVms).SeedAsync();
        var bystander = await _player.Actor().SeedAsync();

        Assert.True(await _player.ServiceFor(holder.Id).CanViewVms([], [], Ct));
        Assert.False(await _player.ServiceFor(bystander.Id).CanViewVms([], [], Ct));
    }
}
