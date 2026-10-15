// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Tests.Support;

namespace Player.Vm.Api.Tests.Features.Vms.Commands;

/// <summary>
/// <c>BulkPowerOperation.Handler</c>'s visibility check (<c>TryCanAccessVm</c>, over the real
/// <c>VmService.CanAccessVm</c> and <c>PlayerService</c>), driven directly. The check answers a Vm the caller
/// cannot see with one <c>"Unauthorized"</c> entry rather than a refusal of the request, so its denial is
/// that entry and a Vm the hypervisor was never asked about. The same contract over HTTP is
/// <see cref="BulkPowerOperationEndpointTests"/>; the hypervisors and the xAPI sink, which leave the process,
/// are substituted.
/// </summary>
public class BulkPowerOperationHandlerTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly PlayerHarness _player = new(Guid.NewGuid(), TestContext.Current.CancellationToken);
    private readonly IVsphereService _vsphere = Substitute.For<IVsphereService>();

    [Fact]
    public async Task BulkPowerOperation_refuses_a_caller_holding_ControlTeamVms_only_on_another_team_with_an_Unauthorized_entry()
    {
        var vm = TestData.VsphereVm();
        await Seed(vm);
        _player.Grant(b => b.Holding(AppTeamPermission.ControlTeamVms, Guid.NewGuid()));

        var response = await PowerOn(vm.Id);

        Assert.Equal("Unauthorized", response.Errors.For(vm.Id));
        Assert.Empty(response.Accepted);
        await _vsphere.DidNotReceive().BulkPowerOperation(Arg.Any<Guid[]>(), Arg.Any<PowerOperation>());
    }

    [Fact]
    public async Task BulkPowerOperation_submits_the_Vm_for_a_caller_holding_exactly_ControlTeamVms_on_its_team()
    {
        var teamId = Guid.NewGuid();
        var vm = TestData.VsphereVm(teamId);
        await Seed(vm);
        _player.Grant(b => b.Holding(AppTeamPermission.ControlTeamVms, teamId));
        _vsphere.BulkPowerOperation(Arg.Any<Guid[]>(), PowerOperation.PowerOn)
            .Returns(new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        var response = await PowerOn(vm.Id);

        Assert.Equal([vm.Id], response.Accepted);
        Assert.Empty(response.Errors);
    }

    /// <summary>A power-on of <paramref name="ids"/> by the caller, over a context of the handler's own.</summary>
    private async Task<BulkPowerOperation.Response> PowerOn(params Guid[] ids)
    {
        await using var context = NewContext();
        var players = _player.Service;
        var vmService = new VmService(
            context, players, _player.Views, _player.User, TestMapper.Mapper,
            new NetworkService(context, players, TestMapper.Mapper));

        var handler = new BulkPowerOperation.Handler(
            _vsphere, Substitute.For<IProxmoxService>(), players, context, vmService, Substitute.For<IXApiService>());

        return await handler.Handle(new BulkPowerOperation.Command { Ids = ids, Operation = PowerOperation.PowerOn }, Ct);
    }
}
