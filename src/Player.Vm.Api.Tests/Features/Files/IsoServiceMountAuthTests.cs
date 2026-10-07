// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Infrastructure.Options;

using VmType = Player.Vm.Api.Domain.Models.VmType;
using Player.Vm.Api.Tests.Support;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Tests.Features.Files;

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

    private readonly PlayerHarness _player = new(Guid.NewGuid(), TestContext.Current.CancellationToken);
    private readonly IIsoProvider _provider = Substitute.For<IIsoProvider>();
    private PlayerServiceSpy _spy;

    private IViewService _viewService => _player.Views;

    public IsoServiceMountAuthTests()
    {
        _provider.Enabled.Returns(true);
        _provider.ProviderType.Returns(VmType.Proxmox);

        // The VM's teams place it in one View, and each team is in that View. Individual tests override
        // whichever of these the case is about.
        _viewService.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([ViewId]);
        _player.Directory.PlaceTeam(TeamA, ViewId);
        _player.Directory.PlaceTeam(TeamB, ViewId);
    }

    private IsoService Service() =>
        new(PlayerServiceSpy.Wrap(_player.Service, out _spy), _viewService, [_provider], new IsoUploadOptions(),
            NullLogger<IsoService>.Instance);

    // What the provider decoded out of the submitted value. The value itself is irrelevant here - the
    // point of the design is that only these three fields survive it.
    private void Decodes(Guid viewId, Guid scopeId, string filename = "tools.iso")
    {
        _provider.ResolveMountTargetAsync(VmId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IsoMountTarget(viewId, scopeId, filename, Token));
    }

    // ControlTeamVms on each team, and nothing View-wide.
    private void CallerCanControlVms(params Guid[] teamIds)
    {
        foreach (var teamId in teamIds)
            _player.Grant(b => b.Holding(AppTeamPermission.ControlTeamVms, teamId));
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
        CallerCanControlVms(TeamA);

        Assert.Equal(Token, await Mount(TeamA));
    }

    // The check that used to be missing: control of the VM is not control of the Vms of the team whose
    // ISO is being published to it.
    [Fact]
    public async Task TeamScopedIso_WithoutControlOfThatTeamsVms_IsRefused()
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
        CallerCanControlVms(TeamA, TeamB);

        Assert.Equal(Token, await Mount(TeamA));
    }

    // Rights over the VM's own team are not rights over the team whose ISO is being mounted, so the
    // widened rule above still turns on a check of the scope's team specifically.
    [Fact]
    public async Task TeamScopedIso_OnATeamTheCallerMayNotEdit_IsRefused_EvenWithRightsOnTheVmsTeam()
    {
        Decodes(ViewId, TeamB);
        CallerCanControlVms(TeamA);

        await AssertRefused(TeamA);
    }

    // A View-scoped ISO's audience is the whole View, which contains the VM, and the caller has already
    // been authorized to control a VM in it - so no team check applies, and none is made.
    [Fact]
    public async Task ViewScopedIso_InAViewOfThisVm_IsAllowedWithNoTeamCheck()
    {
        Decodes(ViewId, ViewId);

        Assert.Equal(Token, await Mount(TeamA));

        Assert.Empty(_spy.CallsTo(nameof(IPlayerService.CanControlVms)));
    }

    [Fact]
    public async Task ViewScopedIso_FromAViewThisVmIsNotIn_IsRefused()
    {
        Decodes(OtherViewId, OtherViewId);

        await AssertRefused(TeamA);
    }

    // A hand-built value pairing this VM's View with a team from somewhere else encodes a scope no
    // upload could have produced. Caught even though the caller may well be able to control that team's Vms.
    [Fact]
    public async Task ForgedViewAndTeamPair_IsRefused()
    {
        Decodes(ViewId, TeamA);
        CallerCanControlVms(TeamA);
        _player.Directory.PlaceTeam(TeamA, OtherViewId);

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

    // The teams the caller can see in the View: the first is its primary team, the rest are scoped onto
    // it (which ViewTeam on the primary team lets it see). None means no primary team, so nothing visible.
    private void CallerIsIn(Guid viewId, params Guid[] teamIds)
    {
        if (teamIds.Length == 0)
            return;

        foreach (var teamId in teamIds)
            _player.Directory.PlaceTeam(teamId, viewId);

        _player.Grant(b =>
        {
            b.PrimaryOn(teamIds[0]);

            if (teamIds.Length > 1)
                b.Holding(AppTeamPermission.ViewTeam, teamIds[0]);

            foreach (var other in teamIds.Skip(1))
                b.SeesScoped(other);

            return b;
        });
    }

    // Every team in the View, whether or not the caller is a member, named as the privileged all-teams
    // listing reports them. player.api answers that listing only for a caller holding ViewViews or ViewView
    // on the View: see MayListTheViewsTeams.
    private void ViewHasTeams(Guid viewId, params Guid[] teamIds)
    {
        foreach (var teamId in teamIds)
            _player.Directory.PlaceTeam(teamId, viewId, $"all-{teamId.ToString()[..4]}");
    }

    // The system ViewViews, which player.api's all-teams listing accepts and which grants nothing on a Vm.
    private void MayListTheViewsTeams() => _player.Grant(b => b.Holding(AppSystemPermission.ViewViews));

    // Control of every team's Vms through the system ControlVms, with no team or membership in the View.
    private void ControlsEveryVm() => _player.Grant(b => b.Holding(AppSystemPermission.ControlVms));

    private void ViewExists(Guid viewId) => _player.Directory.AddView(viewId);

    // player.api refusing the privileged all-teams listing.
    private void AllTeamsListingIsRefused(Guid viewId) =>
        _player.Client.GetViewTeamsAsync(viewId, Arg.Any<CancellationToken>())
            .Returns<ICollection<Team>>(_ => throw new ApiException("Forbidden", 403, null, new Dictionary<string, IEnumerable<string>>(), null));

    private Task<IReadOnlyList<ViewTeams>> ResolveViewTeams(params Guid[] vmTeamIds) =>
        Service().ResolveViewTeamsForVmAsync(vmTeamIds, CancellationToken.None);

    [Fact]
    public async Task Picker_OffersAVmTeamTheCallerMayEdit()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA);
        CallerCanControlVms(TeamA);

        var views = await ResolveViewTeams(TeamA);

        var view = Assert.Single(views);
        Assert.Equal(TeamA, Assert.Single(view.Teams).Id);
    }

    // The under-permissive gap: a view-admin who is not a member of the VM's team may still control it, and
    // uploaded the ISO in the first place. The name then comes from the privileged all-teams listing.
    [Fact]
    public async Task Picker_OffersAVmTeamTheCallerIsNotAMemberOf_NamedFromTheAllTeamsListing()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId);   // no memberships at all
        _player.Directory.PlaceTeam(TeamA, ViewId, "Red Team");
        _player.Directory.PlaceTeam(TeamB, OtherViewId);
        MayListTheViewsTeams();
        ControlsEveryVm();

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
        CallerIsIn(ViewId);   // no memberships at all
        ControlsEveryVm();
        AllTeamsListingIsRefused(ViewId);

        var team = Assert.Single(Assert.Single(await ResolveViewTeams(TeamA)).Teams);

        Assert.Equal(TeamA, team.Id);
        Assert.Equal(TeamA.ToString(), team.Name);   // degraded to an id-only row rather than dropped
    }

    // An admin on a team the VM is not on sees their own team's ISOs: the mount accepts that scope, so
    // the picker has to offer it.
    [Fact]
    public async Task Picker_OffersACallerTeamTheVmIsNotOn()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA, TeamB);
        CallerCanControlVms(TeamA, TeamB);

        var teams = Assert.Single(await ResolveViewTeams(TeamA)).Teams;

        Assert.Equal(new[] { TeamA, TeamB }, teams.Select(t => t.Id).OrderBy(id => id.ToString()).ToArray());
    }

    /// <summary>A team the caller holds only a scoped claim on is not offered without the all-teams listing.</summary>
    [Fact]
    public async Task Picker_DoesNotOfferATeamTheCallerIsOnlyScopedInto_WithoutTheAllTeamsListing()
    {
        ViewExists(ViewId);
        ViewHasTeams(ViewId, TeamA, TeamB);

        // As player.api reports it: the caller's primary team A grants ControlTeamVms, and team B shares
        // itself with A, so the claim on B carries A's permissions. Nothing lets it read the all-teams listing.
        _player.Grant(b => b.PrimaryOn(TeamA).Holding(AppTeamPermission.ControlTeamVms, TeamA).SeesScoped(TeamB));

        var team = Assert.Single(Assert.Single(await ResolveViewTeams(TeamA)).Teams);

        Assert.Equal(TeamA, team.Id);
    }

    // The same caller allowed the privileged listing: the scoped team is a candidate, admitted by its claim,
    // and named as that listing names it.
    [Fact]
    public async Task Picker_OffersATeamTheCallerIsOnlyScopedInto_WhenItMayListTheViewsTeams()
    {
        ViewExists(ViewId);
        ViewHasTeams(ViewId, TeamA, TeamB);
        _player.Grant(b => b.PrimaryOn(TeamA).Holding(AppTeamPermission.ControlTeamVms, TeamA).SeesScoped(TeamB));
        MayListTheViewsTeams();

        var teams = Assert.Single(await ResolveViewTeams(TeamA)).Teams;

        Assert.Equal("all-bbbb", Assert.Single(teams, t => t.Id == TeamB).Name);
        Assert.Contains(teams, t => t.Id == TeamA);
    }

    // Widening the candidates to the whole View does not widen who may use them.
    [Fact]
    public async Task Picker_OmitsAViewTeamTheCallerMayNotEdit()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA);
        ViewHasTeams(ViewId, TeamA, TeamB);
        CallerCanControlVms(TeamA);
        MayListTheViewsTeams();

        var teams = Assert.Single(await ResolveViewTeams(TeamA)).Teams;

        Assert.Equal(TeamA, Assert.Single(teams).Id);
    }

    // A refusal from the privileged listing must not lose a team the caller is a member of, which is the
    // case the picker exists for.
    [Fact]
    public async Task Picker_StillOffersACallerTeam_WhenTheAllTeamsListingIsRefused()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA, TeamB);
        CallerCanControlVms(TeamA, TeamB);
        AllTeamsListingIsRefused(ViewId);

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
        _player.Directory.PlaceTeam(TeamB, ViewId, "student");
        _player.Directory.PlaceTeam(TeamA, ViewId, "Admin");
        MayListTheViewsTeams();
        ControlsEveryVm();

        var teams = Assert.Single(await ResolveViewTeams(TeamB)).Teams;

        Assert.Equal(new[] { "Admin", "student" }, teams.Select(t => t.Name).ToArray());
    }

    // A team offering nothing still leaves the View's own public ISOs mountable, so the View stays as
    // long as the caller can see it at all.
    [Fact]
    public async Task Picker_KeepsTheViewForItsPublicIsos_WhenNoTeamIsAdmitted()
    {
        ViewExists(ViewId);
        CallerIsIn(ViewId, TeamA);   // a member, but without Vm control rights

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
        _player.Directory.PlaceTeam(TeamA, ViewId, "Red Team");
        _player.Directory.PlaceTeam(TeamB, OtherViewId);
        _player.Client.GetUserViewTeamsAsync(ViewId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<ICollection<Team>>(_ => throw new ApiException("Not Found", 404, null, new Dictionary<string, IEnumerable<string>>(), null));

        // Control through the system permission, which answers before PlayerService reads the View's
        // teams: with the View's teams refused, a team or View grant could not be read either.
        _player.Grant(b => b.Holding(Player.Vm.Api.Infrastructure.Authorization.AppSystemPermission.ControlVms));

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
        CallerCanControlVms(TeamA, TeamB);

        _viewService.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([ViewId, OtherViewId]);

        var byView = (await ResolveViewTeams(TeamA, TeamB)).ToDictionary(v => v.View.Id);

        Assert.Equal(TeamA, Assert.Single(byView[ViewId].Teams).Id);
        Assert.Equal(TeamB, Assert.Single(byView[OtherViewId].Teams).Id);
    }
}
