// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using AppSystemPermission = Player.Vm.Api.Infrastructure.Authorization.AppSystemPermission;
using AppViewPermission = Player.Vm.Api.Infrastructure.Authorization.AppViewPermission;
using AppTeamPermission = Player.Vm.Api.Infrastructure.Authorization.AppTeamPermission;
using ViewNetworkEntity = Player.Vm.Api.Domain.Models.ViewNetwork;

namespace Player.Vm.Api.Tests;

/// <summary>
/// NetworkService decides which networks a caller may see and which a VM's NIC may be attached to.
///
/// Two things here are worth more than the gates themselves. The read gate accepts either ViewNetworks
/// or ManageNetworks while the write gates accept only ManageNetworks, so a caller holding view rights
/// must be able to read and must not be able to write - a distinction that a substitute returning a flat
/// true or false cannot show. And GetEffectiveNetworkPermissions is what stops a team attaching a NIC to
/// another team's network: it is the only thing standing between a segmented range and a flat one.
///
/// The permission substitute below models player.api's actual rule - any one of the listed permissions
/// satisfies the check - rather than answering yes or no per call site, so the arrays the production code
/// passes are what decides the outcome.
/// </summary>
public class NetworkServiceTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly IPlayerService _player = Substitute.For<IPlayerService>();

    private NetworkService Service => new(Db, _player, TestMapper.Value);

    #region Read and write gates

    [Fact]
    public async Task Reads_WithoutAnyNetworkPermission_AreForbidden()
    {
        var network = await SeededNetwork();
        Holding();

        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetByViewId(network.ViewId, Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetById(network.ViewId, network.Id, Ct));
    }

    [Fact]
    public async Task Reads_WithAViewOnlyPermission_AreAllowed()
    {
        var network = await SeededNetwork();
        Holding(AppSystemPermission.ViewNetworks);

        Assert.Equal<Guid>([network.Id], (await Service.GetByViewId(network.ViewId, Ct)).Select(x => x.Id).ToArray());
        Assert.Equal(network.Id, (await Service.GetById(network.ViewId, network.Id, Ct)).Id);
    }

    // ManageNetworks is accepted for reads in its own right, so a manager does not also need the view
    // permission granted alongside it.
    [Fact]
    public async Task Reads_WithOnlyTheManagePermission_AreAllowed()
    {
        var network = await SeededNetwork();
        Holding(AppSystemPermission.ManageNetworks);

        Assert.Equal(network.Id, (await Service.GetById(network.ViewId, network.Id, Ct)).Id);
    }

    /// <summary>
    /// The asymmetry that matters: view rights over a view's networks do not carry the right to change
    /// them. Driven as a theory so a write path added without a manage gate shows up as a missing case.
    /// </summary>
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Writes_WithAViewOnlyPermission_AreForbidden(string operation)
    {
        var network = await SeededNetwork();
        Holding(AppSystemPermission.ViewNetworks);

        var service = Service;

        Task act = operation switch
        {
            "create" => service.CreateViewNetwork(network.ViewId, new CreateViewNetworkForm { NetworkId = "n" }, Ct),
            "update" => service.UpdateViewNetwork(network.ViewId, network.Id, new UpdateViewNetworkForm { NetworkId = "n" }, Ct),
            "delete" => service.DeleteViewNetwork(network.ViewId, network.Id, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "unhandled operation")
        };

        await Assert.ThrowsAsync<ForbiddenException>(() => act);
    }

    [Fact]
    public async Task Delete_WithTheManagePermission_RemovesTheNetwork()
    {
        var network = await SeededNetwork();
        Holding(AppSystemPermission.ManageNetworks);

        await Service.DeleteViewNetwork(network.ViewId, network.Id, Ct);

        await using var context = NewContext();
        Assert.Empty(context.ViewNetworks.Where(x => x.Id == network.Id));
    }

    /// <summary>
    /// The gate is asked about the view being operated on. Passing the wrong view id would authorize the
    /// call against a view the caller does hold rights in, which no other assertion here would notice
    /// because the substitute ignores the ids it is given.
    /// </summary>
    [Fact]
    public async Task Reads_AskAboutTheRequestedView()
    {
        var viewId = Guid.NewGuid();
        Holding(AppSystemPermission.ViewNetworks);

        await Service.GetByViewId(viewId, Ct);

        await _player.Received().Can(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Is<IEnumerable<Guid>>(views => views.SequenceEqual(new[] { viewId })),
            Arg.Any<AppSystemPermission[]>(),
            Arg.Any<AppViewPermission[]>(),
            Arg.Any<AppTeamPermission[]>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Scoping to the requested view

    // A network belongs to one view. Reaching it through another view has to read as not found, even for
    // a caller who legitimately holds network rights in the view they asked about.
    [Fact]
    public async Task GetById_ForANetworkInAnotherView_IsNotFound()
    {
        var network = await SeededNetwork();
        Holding(AppSystemPermission.ManageNetworks);

        await Assert.ThrowsAsync<EntityNotFoundException<Features.Networks.ViewNetwork>>(
            () => Service.GetById(Guid.NewGuid(), network.Id, Ct));
    }

    [Fact]
    public async Task Delete_ForANetworkInAnotherView_IsNotFound()
    {
        var network = await SeededNetwork();
        Holding(AppSystemPermission.ManageNetworks);

        await Assert.ThrowsAsync<EntityNotFoundException<Features.Networks.ViewNetwork>>(
            () => Service.DeleteViewNetwork(Guid.NewGuid(), network.Id, Ct));
    }

    [Fact]
    public async Task GetByViewId_ReturnsOnlyTheRequestedViewsNetworks()
    {
        var viewId = Guid.NewGuid();
        var mine = Network(viewId, "vlan-10");
        var other = Network(Guid.NewGuid(), "vlan-20");
        await Seed(mine, other);
        Holding(AppSystemPermission.ViewNetworks);

        Assert.Equal<Guid>([mine.Id], (await Service.GetByViewId(viewId, Ct)).Select(x => x.Id).ToArray());
    }

    #endregion

    #region Effective network permissions

    /// <summary>
    /// A caller with view-wide network access may attach a NIC to any network the view defines, including
    /// ones assigned to no team at all.
    /// </summary>
    [Fact]
    public async Task Effective_WithViewNetworkAccess_AllowsEveryNetworkInTheView()
    {
        var viewId = Guid.NewGuid();
        var unassigned = Network(viewId, "vlan-10");
        var otherTeams = Network(viewId, "vlan-20", teamIds: [Guid.NewGuid()]);
        await Seed(unassigned, otherTeams);

        HasViewNetworkAccess(true);

        var effective = await Service.GetEffectiveNetworkPermissions(
            viewId, [Guid.NewGuid()], VmType.Vsphere, Instance, Ct);

        Assert.Equal(["vlan-10", "vlan-20"], effective.AllowedNetworks.Keys.OrderBy(x => x));
    }

    /// <summary>
    /// The segmentation guard. Without view-wide access, a network is only allowed if one of its team ids
    /// is a team the caller is actually on - so a team cannot reach the network of a team beside it, which
    /// is what makes a range with several teams in it separable at all.
    /// </summary>
    [Fact]
    public async Task Effective_WithoutViewNetworkAccess_AllowsOnlyTheCallersOwnTeamsNetworks()
    {
        var viewId = Guid.NewGuid();
        var myTeam = Guid.NewGuid();
        var theirTeam = Guid.NewGuid();

        var mine = Network(viewId, "vlan-10", teamIds: [myTeam]);
        var theirs = Network(viewId, "vlan-20", teamIds: [theirTeam]);
        var unassigned = Network(viewId, "vlan-30");
        await Seed(mine, theirs, unassigned);

        HasViewNetworkAccess(false);
        UserTeams(myTeam);

        var effective = await Service.GetEffectiveNetworkPermissions(
            viewId, [myTeam, theirTeam], VmType.Vsphere, Instance, Ct);

        Assert.Equal(["vlan-10"], effective.AllowedNetworks.Keys);
    }

    // A network shared between two teams is reachable by both, so an intersection is enough - the
    // caller's teams do not have to be the whole of the network's.
    [Fact]
    public async Task Effective_AllowsANetworkSharedWithAnotherTeam()
    {
        var viewId = Guid.NewGuid();
        var myTeam = Guid.NewGuid();
        var shared = Network(viewId, "vlan-10", teamIds: [Guid.NewGuid(), myTeam]);
        await Seed(shared);

        HasViewNetworkAccess(false);
        UserTeams(myTeam);

        var effective = await Service.GetEffectiveNetworkPermissions(
            viewId, [myTeam], VmType.Vsphere, Instance, Ct);

        Assert.Equal(["vlan-10"], effective.AllowedNetworks.Keys);
    }

    /// <summary>
    /// Networks are scoped to a provider and to one instance of it. Two vCenters can both define a
    /// network called dvs-100, and allowing one because the other was permitted would attach a NIC to the
    /// wrong switch on the wrong hypervisor.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Effective_IgnoresNetworksFromAnotherProviderOrInstance(bool hasViewNetworkAccess)
    {
        var viewId = Guid.NewGuid();
        var myTeam = Guid.NewGuid();

        var wanted = Network(viewId, "dvs-100", teamIds: [myTeam]);
        var otherInstance = Network(viewId, "dvs-100", teamIds: [myTeam], providerInstanceId: "vcenter-2");
        var otherProvider = Network(viewId, "dvs-100", teamIds: [myTeam], providerType: VmType.Proxmox);
        var otherView = Network(Guid.NewGuid(), "dvs-100", teamIds: [myTeam]);
        await Seed(wanted, otherInstance, otherProvider, otherView);

        HasViewNetworkAccess(hasViewNetworkAccess);
        UserTeams(myTeam);

        var effective = await Service.GetEffectiveNetworkPermissions(
            viewId, [myTeam], VmType.Vsphere, Instance, Ct);

        // Keyed by network id, so the three rejected rows would have collided with this one had any of
        // them been included - hence the count as well as the key.
        Assert.Equal(["dvs-100"], effective.AllowedNetworks.Keys);
        Assert.Single(effective.AllowedNetworks);
    }

    // The dictionary is a network id to display name map, and the UI renders the value. A null name has
    // to arrive as an empty string rather than a null the caller has to defend against.
    [Fact]
    public async Task Effective_ReportsAnUnnamedNetworkAsAnEmptyName()
    {
        var viewId = Guid.NewGuid();
        var unnamed = Network(viewId, "vlan-10", name: null);
        await Seed(unnamed);

        HasViewNetworkAccess(true);

        var effective = await Service.GetEffectiveNetworkPermissions(
            viewId, [Guid.NewGuid()], VmType.Vsphere, Instance, Ct);

        Assert.Equal(string.Empty, effective.AllowedNetworks["vlan-10"]);
    }

    // No teams in common means nothing is attachable, rather than everything.
    [Fact]
    public async Task Effective_WithNoTeamsInCommon_AllowsNothing()
    {
        var viewId = Guid.NewGuid();
        await Seed(Network(viewId, "vlan-10", teamIds: [Guid.NewGuid()]));

        HasViewNetworkAccess(false);
        UserTeams();

        var effective = await Service.GetEffectiveNetworkPermissions(
            viewId, [Guid.NewGuid()], VmType.Vsphere, Instance, Ct);

        Assert.Empty(effective.AllowedNetworks);
    }

    #endregion

    #region Network names

    /// <summary>
    /// GetNetworkNames has no permission gate of its own - it is called while mapping a VM whose access
    /// has already been decided - so the view, provider and instance filter is the whole of its
    /// isolation. A name resolved across views would disclose another view's network naming.
    /// </summary>
    [Fact]
    public async Task GetNetworkNames_ResolvesOnlyWithinTheRequestedViewAndInstance()
    {
        var viewId = Guid.NewGuid();
        await Seed(
            Network(viewId, "dvs-100", name: "wanted"),
            Network(viewId, "dvs-100", name: "other instance", providerInstanceId: "vcenter-2"),
            Network(Guid.NewGuid(), "dvs-100", name: "other view"));

        var names = await Service.GetNetworkNames(viewId, VmType.Vsphere, Instance, ["dvs-100"], Ct);

        Assert.Equal("wanted", Assert.Single(names).Value);
    }

    // Cast because InlineData takes params object[]: an uncast string[] would be spread into one argument
    // per element rather than passed as the single collection the test takes.
    [Theory]
    [InlineData((object)null)]
    [InlineData((object)new string[0])]
    [InlineData((object)new[] { "", "   " })]
    public async Task GetNetworkNames_WithNothingToResolve_IsEmpty(string[] networkIds)
    {
        await Seed(Network(Guid.NewGuid(), "dvs-100", name: "wanted"));

        Assert.Empty(await Service.GetNetworkNames(Guid.NewGuid(), VmType.Vsphere, Instance, networkIds, Ct));
    }

    #endregion

    #region Update conflicts

    /// <summary>
    /// The view, provider, instance and network id together identify one network, and two rows claiming
    /// the same one would make GetNetworkNames ambiguous and GetEffectiveNetworkPermissions throw on a
    /// duplicate dictionary key. Retargeting a network onto one that already exists is refused.
    /// </summary>
    [Fact]
    public async Task Update_OntoANetworkThatAlreadyExists_IsABadRequest()
    {
        var viewId = Guid.NewGuid();
        var moving = Network(viewId, "dvs-100");
        var occupied = Network(viewId, "dvs-200");
        await Seed(moving, occupied);
        Holding(AppSystemPermission.ManageNetworks);

        var form = new UpdateViewNetworkForm
        {
            ProviderType = VmType.Vsphere,
            ProviderInstanceId = Instance,
            NetworkId = "dvs-200"
        };

        await Assert.ThrowsAsync<BadRequestException>(
            () => Service.UpdateViewNetwork(viewId, moving.Id, form, Ct));
    }

    // Renaming a network in place leaves the identity untouched, so the conflict check must not match the
    // row being updated against itself.
    [Fact]
    public async Task Update_LeavingTheIdentityAlone_Succeeds()
    {
        var viewId = Guid.NewGuid();
        var network = Network(viewId, "dvs-100", name: "before");
        await Seed(network);
        Holding(AppSystemPermission.ManageNetworks);

        var form = new UpdateViewNetworkForm
        {
            ProviderType = VmType.Vsphere,
            ProviderInstanceId = Instance,
            NetworkId = "dvs-100",
            Name = "after"
        };

        Assert.Equal("after", (await Service.UpdateViewNetwork(viewId, network.Id, form, Ct)).Name);
    }

    #endregion

    #region Helpers

    private const string Instance = "vcenter-1";

    /// <summary>
    /// Answers Can the way player.api does: the check passes if the caller holds any one of the
    /// permissions the call site listed. Nothing granted unless a test says so.
    /// </summary>
    private void Holding(params AppSystemPermission[] held) =>
        _player.Can(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<AppSystemPermission[]>(),
            Arg.Any<AppViewPermission[]>(),
            Arg.Any<AppTeamPermission[]>(),
            Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<AppSystemPermission[]>(2).Any(held.Contains));

    private void HasViewNetworkAccess(bool allowed) =>
        _player.HasViewNetworkAccess(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(allowed);

    private void UserTeams(params Guid[] teamIds) =>
        _player.GetUserTeamIds(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IEnumerable<Guid>)teamIds);

    private async Task<ViewNetworkEntity> SeededNetwork()
    {
        var network = Network(Guid.NewGuid(), "dvs-100");
        await Seed(network);

        return network;
    }

    private static ViewNetworkEntity Network(
        Guid viewId,
        string networkId,
        Guid[] teamIds = null,
        string name = "network",
        string providerInstanceId = Instance,
        VmType providerType = VmType.Vsphere) =>
        new()
        {
            Id = Guid.NewGuid(),
            ViewId = viewId,
            NetworkId = networkId,
            Name = name,
            TeamIds = teamIds ?? [],
            ProviderInstanceId = providerInstanceId,
            ProviderType = providerType
        };

    #endregion
}
