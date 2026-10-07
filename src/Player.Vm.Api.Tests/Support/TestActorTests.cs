// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api's actor is an identity and nothing more (permissions are the substituted player.api's answers),
// so its shapes are the identity a request carries: the sub claim, which the application reads as the
// caller's id, and the builder's guards. Checked over a real request, because no claims service of
// vm.api's own derives anything from rows.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute.ClearExtensions;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Authorization;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;
using VmModel = Player.Vm.Api.Features.Vms.Vm;

namespace Player.Vm.Api.Tests.Support;

/// <summary>Tests for <see cref="TestActorBuilder"/>: an actor that is not the caller it claims to be turns an authorization test into a formality.</summary>
public class TestActorTests(DatabaseFixture fixture, VmAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmAppFactory>
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        Factory.PlayerApi.ClearSubstitute();
        Factory.AllowEverything();
    }

    [Fact]
    public void Root_is_the_identity_the_host_answers_for()
    {
        Assert.Equal(Factory.UserId, Root.Id);
    }

    [Fact]
    public async Task WithId_and_WithName_fix_the_identity()
    {
        var id = Guid.NewGuid();

        var actor = await Actor().WithId(id).WithName("Named Actor").SeedAsync();

        Assert.Equal((id, "Named Actor"), (actor.Id, actor.Name));
    }

    [Fact]
    public async Task Two_actors_are_two_identities()
    {
        var first = await Actor().SeedAsync();
        var second = await Actor().SeedAsync();

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void WithName_refuses_an_empty_name()
    {
        Assert.Throws<ArgumentException>(() => Actor().WithName(" "));
    }

    /// <summary>
    /// The actor's id is the caller the application sees: <c>onlyMine</c> keeps the personal Vm whose
    /// owner is this actor and drops the one owned by another.
    /// </summary>
    [Fact]
    public async Task A_request_from_an_actor_is_made_as_its_id()
    {
        var actor = await Actor().SeedAsync();
        var teamId = Guid.NewGuid();
        var mine = PersonalVm(teamId, actor.Id);
        await Seed(mine, PersonalVm(teamId, Guid.NewGuid()));
        Factory.PlayerApi.GetVisibilityContextForTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new VisibilityContext(teamId, false, [teamId]));

        var vms = await ReadAsync<VmModel[]>(
            await Client(actor).GetAsync($"api/teams/{teamId}/vms?onlyMine=true", Ct));

        Assert.Equal([mine.Id], vms.Select(x => x.Id));
    }

    /// <summary>Every question is answered from the grants: the one held passes, its neighbours do not.</summary>
    [Fact]
    public async Task Holding_answers_every_permission_question_from_exactly_its_grants()
    {
        var (teamId, viewId) = (Guid.NewGuid(), Guid.NewGuid());

        await Actor().Holding(new PlayerPermissions().Holding(AppViewPermission.ViewViewMaps)).SeedAsync();

        Assert.Equal(
            (true, false, false, false),
            (await Factory.PlayerApi.CanViewMaps([teamId], [viewId], Ct),
                await Factory.PlayerApi.CanManageMaps([teamId], [viewId], Ct),
                await Factory.PlayerApi.CanViewVms([teamId], [viewId], Ct),
                await Factory.PlayerApi.CanViewMaps([], [], Ct)));
    }

    [Fact]
    public async Task Holding_a_team_permission_answers_only_for_that_team()
    {
        var (mine, other) = (Guid.NewGuid(), Guid.NewGuid());

        await Actor().Holding(new PlayerPermissions().Holding(AppTeamPermission.ManageTeam, mine)).SeedAsync();

        Assert.Equal(
            (true, false),
            (await Factory.PlayerApi.CanManageTeams([mine], Ct), await Factory.PlayerApi.CanManageTeams([other], Ct)));
    }

    [Fact]
    public async Task Holding_a_view_permission_answers_for_a_team_placed_in_that_view_only()
    {
        var (view, otherView, team, teamInOtherView) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await Actor()
            .Holding(new PlayerPermissions()
                .Holding(AppViewPermission.ControlViewVms, view)
                .InView(team, view)
                .InView(teamInOtherView, otherView))
            .SeedAsync();

        Assert.Equal(
            (true, false, false),
            (await Factory.PlayerApi.CanControlVms([team], Ct),
                await Factory.PlayerApi.CanControlVms([teamInOtherView], Ct),
                await Factory.PlayerApi.CanControlVms([Guid.NewGuid()], Ct)));
    }

    [Fact]
    public async Task Holding_a_system_permission_answers_everywhere()
    {
        await Actor().Holding(new PlayerPermissions().Holding(AppSystemPermission.ViewVms)).SeedAsync();

        Assert.Equal(
            (true, true),
            (await Factory.PlayerApi.CanViewVms([], [], Ct), await Factory.PlayerApi.CanViewVms([Guid.NewGuid()], null, Ct)));
    }

    [Fact]
    public void Holding_twice_throws()
    {
        var builder = Actor().Holding(new PlayerPermissions());

        Assert.Throws<InvalidOperationException>(() => builder.Holding(new PlayerPermissions()));
    }

    private static VmEntity PersonalVm(Guid teamId, Guid ownerId)
    {
        var vm = TestData.VsphereVm(teamId);
        vm.UserId = ownerId;

        return vm;
    }
}
