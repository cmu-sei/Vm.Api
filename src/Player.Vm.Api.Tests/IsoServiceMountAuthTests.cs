// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Infrastructure.Options;
using Xunit;

using VmType = Player.Vm.Api.Domain.Models.VmType;

namespace Player.Vm.Api.Tests;

// Who may mount which ISO on which VM, and the picker that has to agree with that answer.
//
// The scope encoded in the submitted value is checked for being reachable from the VM - its View
// contains the VM, its team is in that View - and then against the caller's rights over it. The
// scope's team need not be one of the VM's: publishing one team's ISO to another's console is a
// choice left to a caller authorized over both. The provider decoders (ProxmoxIsoProviderTests /
// VsphereIsoProviderTests) cover turning a string into a scope; these cover what is done with the
// scope once it is known.
public class IsoServiceMountAuthTests
{
    private static readonly Guid VmId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherViewId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid TeamA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TeamB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private const string Token = "nfs:iso/canonical.iso";

    private readonly IPlayerService _playerService = Substitute.For<IPlayerService>();
    private readonly IViewService _viewService = Substitute.For<IViewService>();
    private readonly IIsoProvider _provider = Substitute.For<IIsoProvider>();

    public IsoServiceMountAuthTests()
    {
        _provider.Enabled.Returns(true);
        _provider.ProviderType.Returns(VmType.Proxmox);

        // The VM's teams place it in one View, and each team is in that View. Individual tests override
        // whichever of these the case is about.
        _viewService.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([ViewId]);
        _viewService.GetViewIdForTeam(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ViewId);

        // Deny by default, so a test that means to allow something has to say so.
        _playerService.CanEditTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(false);
    }

    private IsoService Service() =>
        new(_playerService, _viewService, [_provider], new IsoUploadOptions(),
            NullLogger<IsoService>.Instance);

    // What the provider decoded out of the submitted value. The value itself is irrelevant here - the
    // point of the design is that only these three fields survive it.
    private void Decodes(Guid viewId, Guid scopeId, string filename = "tools.iso")
    {
        _provider.ResolveMountTargetAsync(VmId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IsoMountTarget(viewId, scopeId, filename, Token));
    }

    private void CallerCanEdit(params Guid[] teamIds)
    {
        foreach (var teamId in teamIds)
        {
            _playerService
                .CanEditTeams(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(teamId)), Arg.Any<CancellationToken>())
                .Returns(true);
        }
    }

    private Task<string> Mount(params Guid[] vmTeamIds) =>
        Service().ResolveMountValueAsync(VmId, VmType.Proxmox, vmTeamIds, "submitted", CancellationToken.None);

    private async Task AssertRefused(params Guid[] vmTeamIds)
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Mount(vmTeamIds));

        // Deliberately the same message for every refusal: a specific one would tell a caller probing
        // for other tenants' ISOs exactly how far they got.
        Assert.Equal("The specified iso is not available to this Vm", ex.Message);
    }

    // The canonical token the provider rebuilt is what gets mounted - never the caller's string.
    [Fact]
    public async Task TeamScopedIso_OnATeamOfThisVm_WithEditRights_MountsTheRebuiltToken()
    {
        Decodes(ViewId, TeamA);
        CallerCanEdit(TeamA);

        Assert.Equal(Token, await Mount(TeamA));
    }

    // The check that used to be missing: edit rights on the VM are not edit rights on the team whose
    // ISO is being published to it.
    [Fact]
    public async Task TeamScopedIso_WithoutEditRightsOnThatTeam_IsRefused()
    {
        Decodes(ViewId, TeamA);

        await AssertRefused(TeamA);
    }

    // The reason the VM's teams are not the test: an admin holding rights over both teams may mount
    // their own team's ISO on a student team's VM. It reaches that VM's console because they chose to
    // publish it there, which is theirs to choose.
    [Fact]
    public async Task TeamScopedIso_OnATeamTheCallerMayEditButTheVmIsNotOn_IsAllowed()
    {
        Decodes(ViewId, TeamB);
        CallerCanEdit(TeamA, TeamB);

        Assert.Equal(Token, await Mount(TeamA));
    }

    // Rights over the VM's own team are not rights over the team whose ISO is being mounted, so the
    // widened rule above still turns on a check of the scope's team specifically.
    [Fact]
    public async Task TeamScopedIso_OnATeamTheCallerMayNotEdit_IsRefused_EvenWithRightsOnTheVmsTeam()
    {
        Decodes(ViewId, TeamB);
        CallerCanEdit(TeamA);

        await AssertRefused(TeamA);
    }

    // A View-scoped ISO's audience is the whole View, which contains the VM, and the caller has already
    // been authorized to edit a VM in it - so no team check applies, and none is made.
    [Fact]
    public async Task ViewScopedIso_InAViewOfThisVm_IsAllowedWithNoTeamCheck()
    {
        Decodes(ViewId, ViewId);

        Assert.Equal(Token, await Mount(TeamA));

        await _playerService.DidNotReceive()
            .CanEditTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ViewScopedIso_FromAViewThisVmIsNotIn_IsRefused()
    {
        Decodes(OtherViewId, OtherViewId);

        await AssertRefused(TeamA);
    }

    // A hand-built value pairing this VM's View with a team from somewhere else encodes a scope no
    // upload could have produced. Caught even though the caller may well be able to edit that team.
    [Fact]
    public async Task ForgedViewAndTeamPair_IsRefused()
    {
        Decodes(ViewId, TeamA);
        CallerCanEdit(TeamA);
        _viewService.GetViewIdForTeam(TeamA, Arg.Any<CancellationToken>()).Returns(OtherViewId);

        await AssertRefused(TeamA);
    }

    // Everything the decoders reject - another storage, a disk image, a traversal, a foreign naming
    // scheme - arrives here as a null target.
    [Fact]
    public async Task AValueTheProviderDoesNotRecognize_IsRefused()
    {
        _provider.ResolveMountTargetAsync(VmId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IsoMountTarget)null);

        await AssertRefused(TeamA);
    }

    // No ISO storage configured for the hypervisor this VM runs on means there is no such thing as a
    // Player-managed ISO for it, so nothing can be authorized.
    [Fact]
    public async Task NoEnabledProviderForTheVmsHypervisor_IsRefused()
    {
        _provider.Enabled.Returns(false);
        Decodes(ViewId, ViewId);

        await AssertRefused(TeamA);
    }

    [Fact]
    public async Task AProviderForAnotherHypervisorIsNotConsulted()
    {
        _provider.ProviderType.Returns(VmType.Vsphere);
        Decodes(ViewId, ViewId);

        await AssertRefused(TeamA);

        await _provider.DidNotReceive().ResolveMountTargetAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- ResolveViewTeamsForVmAsync: the picker has to offer exactly what a mount will accept ----

    private void CallerIsIn(Guid viewId, params Guid[] teamIds)
    {
        _playerService.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(teamIds.Select(id => new Team { Id = id, Name = $"team-{id.ToString()[..4]}" }));
    }

    // The privileged all-teams listing: every team in the View, whether or not the caller is a member.
    private void ViewHasTeams(Guid viewId, params Guid[] teamIds)
    {
        _playerService.GetAllTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(teamIds.Select(id => new Team { Id = id, Name = $"all-{id.ToString()[..4]}" }));
    }

    private void ViewExists(Guid viewId)
    {
        _playerService.GetViewByIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(new View { Id = viewId, Name = $"view-{viewId.ToString()[..4]}" });
    }

    private Task<IReadOnlyList<ViewTeams>> ResolveViewTeams(params Guid[] vmTeamIds) =>
        Service().ResolveViewTeamsForVmAsync(vmTeamIds, CancellationToken.None);

    [Fact]
    public async Task Picker_OffersAVmTeamTheCallerMayEdit()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA);
        CallerCanEdit(TeamA);

        var views = await ResolveViewTeams(TeamA);

        var view = Assert.Single(views);
        Assert.Equal(TeamA, Assert.Single(view.Teams).Id);
    }

    // The under-permissive gap: a view-admin who is not a member of the VM's team may still edit it, and
    // uploaded the ISO in the first place. The name then comes from the privileged all-teams listing.
    [Fact]
    public async Task Picker_OffersAVmTeamTheCallerIsNotAMemberOf_NamedFromTheAllTeamsListing()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId);   // no memberships at all
        CallerCanEdit(TeamA);
        _playerService.GetAllTeamsByViewIdAsync(ViewId, Arg.Any<CancellationToken>())
            .Returns([new Team { Id = TeamA, Name = "Red Team" }]);

        var views = await ResolveViewTeams(TeamA);

        var team = Assert.Single(Assert.Single(views).Teams);
        Assert.Equal(TeamA, team.Id);
        Assert.Equal("Red Team", team.Name);
    }

    // vm.api believing the caller holds view-level authority is no guarantee player.api agrees, and a
    // refusal from the privileged endpoint must not lose a team the mount will accept.
    [Fact]
    public async Task Picker_StillOffersTheTeam_WhenTheAllTeamsListingIsRefused()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId);
        CallerCanEdit(TeamA);
        _playerService.GetAllTeamsByViewIdAsync(ViewId, Arg.Any<CancellationToken>())
            .Returns<IEnumerable<Team>>(_ => throw new Exception("403 from player.api"));

        var team = Assert.Single(Assert.Single(await ResolveViewTeams(TeamA)).Teams);

        Assert.Equal(TeamA, team.Id);
        Assert.Equal(TeamA.ToString(), team.Name);   // degraded to an id-only row rather than dropped
    }

    // The reported bug: an admin on a team the VM is not on could not see their own team's ISOs. The
    // mount now accepts that scope, so the picker has to offer it.
    [Fact]
    public async Task Picker_OffersACallerTeamTheVmIsNotOn()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA, TeamB);
        CallerCanEdit(TeamA, TeamB);

        var teams = Assert.Single(await ResolveViewTeams(TeamA)).Teams;

        Assert.Equal(new[] { TeamA, TeamB }, teams.Select(t => t.Id).OrderBy(id => id.ToString()).ToArray());
    }

    // Membership is not the test - CanUseTeamIsoAsync is - so a caller scoped into a team without being
    // a member of it gets its ISOs. Only the privileged listing knows the team exists.
    [Fact]
    public async Task Picker_OffersATeamTheCallerIsOnlyScopedInto()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId);   // no memberships at all
        ViewHasTeams(ViewId, TeamA, TeamB);
        CallerCanEdit(TeamB);

        var team = Assert.Single(Assert.Single(await ResolveViewTeams(TeamA)).Teams);

        Assert.Equal(TeamB, team.Id);
        Assert.Equal("all-bbbb", team.Name);
    }

    // Widening the candidates to the whole View does not widen who may use them.
    [Fact]
    public async Task Picker_OmitsAViewTeamTheCallerMayNotEdit()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA);
        ViewHasTeams(ViewId, TeamA, TeamB);
        CallerCanEdit(TeamA);

        var teams = Assert.Single(await ResolveViewTeams(TeamA)).Teams;

        Assert.Equal(TeamA, Assert.Single(teams).Id);
    }

    // A refusal from the privileged listing must not lose a team the caller is a member of, which is the
    // case the reported bug was about.
    [Fact]
    public async Task Picker_StillOffersACallerTeam_WhenTheAllTeamsListingIsRefused()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA, TeamB);
        CallerCanEdit(TeamA, TeamB);
        _playerService.GetAllTeamsByViewIdAsync(ViewId, Arg.Any<CancellationToken>())
            .Returns<IEnumerable<Team>>(_ => throw new Exception("403 from player.api"));

        var teams = Assert.Single(await ResolveViewTeams(TeamA)).Teams;

        Assert.Equal(new[] { TeamA, TeamB }, teams.Select(t => t.Id).OrderBy(id => id.ToString()).ToArray());
    }

    // Candidate order is player.api's listing order, so the rows are sorted by name to keep a team in the
    // same place in the picker from one View to the next.
    [Fact]
    public async Task Picker_SortsTeamsByName()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId);
        _playerService.GetAllTeamsByViewIdAsync(ViewId, Arg.Any<CancellationToken>())
            .Returns([
                new Team { Id = TeamB, Name = "student" },
                new Team { Id = TeamA, Name = "Admin" }]);
        CallerCanEdit(TeamA, TeamB);

        var teams = Assert.Single(await ResolveViewTeams(TeamB)).Teams;

        Assert.Equal(new[] { "Admin", "student" }, teams.Select(t => t.Name).ToArray());
    }

    // A team offering nothing still leaves the View's own public ISOs mountable, so the View stays as
    // long as the caller can see it at all.
    [Fact]
    public async Task Picker_KeepsTheViewForItsPublicIsos_WhenNoTeamIsAdmitted()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA);   // a member, but without edit rights

        var view = Assert.Single(await ResolveViewTeams(TeamA));

        Assert.Equal(ViewId, view.View.Id);
        Assert.Empty(view.Teams);
    }

    // Nothing admitted and no memberships either: no reason to think the caller can see this View.
    [Fact]
    public async Task Picker_DropsAViewTheCallerHasNeitherTeamsNorRightsIn()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId);

        Assert.Empty(await ResolveViewTeams(TeamA));
    }

    // GetTeamsByViewIdAsync returns null when Player does not know the View, which must not deref.
    [Fact]
    public async Task Picker_ToleratesAnUnknownViewsNullTeamList()
    {
        ViewExists(ViewId);
        _playerService.GetTeamsByViewIdAsync(ViewId, Arg.Any<CancellationToken>())
            .Returns((IEnumerable<Team>)null);
        CallerCanEdit(TeamA);
        _playerService.GetAllTeamsByViewIdAsync(ViewId, Arg.Any<CancellationToken>())
            .Returns([new Team { Id = TeamA, Name = "Red Team" }]);

        var team = Assert.Single(Assert.Single(await ResolveViewTeams(TeamA)).Teams);

        Assert.Equal(TeamA, team.Id);
    }

    // A VM shared across two Views must not offer one View's teams under the other, or a mount would
    // then refuse what the picker showed.
    [Fact]
    public async Task Picker_KeepsEachViewsTeamsToThatView()
    {
        ViewExists(ViewId);
        ViewExists(OtherViewId);
        CallerIsIn(ViewId, TeamA);
        CallerIsIn(OtherViewId, TeamB);
        CallerCanEdit(TeamA, TeamB);

        _viewService.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([ViewId, OtherViewId]);
        _viewService.GetViewIdForTeam(TeamA, Arg.Any<CancellationToken>()).Returns(ViewId);
        _viewService.GetViewIdForTeam(TeamB, Arg.Any<CancellationToken>()).Returns(OtherViewId);

        var byView = (await ResolveViewTeams(TeamA, TeamB)).ToDictionary(v => v.View.Id);

        Assert.Equal(TeamA, Assert.Single(byView[ViewId].Teams).Id);
        Assert.Equal(TeamB, Assert.Single(byView[OtherViewId].Teams).Id);
    }
}
