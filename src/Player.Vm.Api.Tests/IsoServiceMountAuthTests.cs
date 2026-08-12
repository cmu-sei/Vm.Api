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
// Mounting publishes an ISO's contents to everyone who can reach the VM's console - the VM's teams -
// so the scope encoded in the submitted value is checked against the VM first and the caller second.
// The provider decoders (ProxmoxIsoProviderTests / VsphereIsoProviderTests) cover turning a string
// into a scope; these cover what is done with the scope once it is known.
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

    // The over-permissive gap the listing-based whitelist had: a caller in both teams could mount team
    // B's ISO into a team-A-only VM, exposing it to all of team A.
    [Fact]
    public async Task TeamScopedIso_OnATeamTheCallerIsInButTheVmIsNot_IsRefused()
    {
        Decodes(ViewId, TeamB);
        CallerCanEdit(TeamA, TeamB);

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

    // The mirror of the over-permissive mount case: a team of the caller's that the VM is not on is not
    // the VM's to expose, so the picker must not offer it either.
    [Fact]
    public async Task Picker_OmitsACallerTeamTheVmIsNotOn()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA, TeamB);
        CallerCanEdit(TeamA, TeamB);

        var teams = Assert.Single(await ResolveViewTeams(TeamA)).Teams;

        Assert.Equal(TeamA, Assert.Single(teams).Id);
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
