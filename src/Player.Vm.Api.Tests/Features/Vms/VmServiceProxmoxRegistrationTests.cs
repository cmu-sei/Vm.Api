// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Tests.Support;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;
// Spelled out because the test project has its own Infrastructure namespace, which wins over the
// application's when the name is written relatively.
using AppSystemPermission = Player.Vm.Api.Infrastructure.Authorization.AppSystemPermission;

namespace Player.Vm.Api.Tests.Features.Vms;

/// <summary>
/// VmService checks that a vmid is free before registering it, but another request can register the
/// same vmid between that check and the save. VmsEndpointTests covers the check; this class covers the
/// race, which no HTTP test can line up reliably. Each test registers the vmid from a second context
/// while the service's own save is starting, after the check has passed.
/// </summary>
public class VmServiceProxmoxRegistrationTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly Guid Caller = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly PlayerHarness _player = new(Caller, TestContext.Current.CancellationToken, Principal(Caller));

    private VmService Service =>
        new(Db, _player.Service, _player.Views, Principal(Caller), TestConsoleUrls.Options, Substitute.For<INetworkService>());

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _player.Grant(b => b.Holding(AppSystemPermission.ManageViews));
    }

    [Fact]
    public async Task Create_losing_the_race_for_a_Proxmox_id_is_a_conflict_and_stores_nothing()
    {
        var rival = TestData.ProxmoxVm(vmid: 100);
        RegisterWhenTheServiceSaves(rival);
        var form = new VmCreateForm
        {
            Id = Guid.NewGuid(),
            Name = "new",
            TeamIds = [Guid.NewGuid()],
            ProxmoxVmInfo = new Player.Vm.Api.Features.Vms.ProxmoxVmInfo { Id = 100, Node = "pve-2" }
        };

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service.CreateAsync(form, Ct));

        Assert.Equal("Proxmox VM 100 is already registered to another virtual machine.", ex.Message);
        await using var context = NewContext();
        Assert.Equal(rival.Id, Assert.Single(context.Vms).Id);
        Assert.Equal(rival.Id, Assert.Single(context.ProxmoxVmInfo).VmId);
    }

    /// <summary>
    /// Moving a VM deletes its old row and inserts the new one in a single save, so losing the race
    /// must leave the VM on its old vmid rather than on none.
    /// </summary>
    [Fact]
    public async Task Update_losing_the_race_for_a_Proxmox_id_is_a_conflict_and_keeps_the_old_one()
    {
        var vm = TestData.ProxmoxVm(vmid: 101);
        var name = vm.Name;
        await Seed(vm);
        var rival = TestData.ProxmoxVm(vmid: 102);
        RegisterWhenTheServiceSaves(rival);
        var form = new VmUpdateForm
        {
            Name = "after",
            ProxmoxVmInfo = new Player.Vm.Api.Features.Vms.ProxmoxVmInfo { Id = 102, Node = "pve-2" }
        };

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service.UpdateAsync(vm.Id, form, Ct));

        Assert.Equal("Proxmox VM 102 is already registered to another virtual machine.", ex.Message);
        await using var context = NewContext();
        // Not vm.Name: Seed tracked vm in the service's context, so the update renamed it in memory.
        Assert.Equal(name, context.Vms.Single(x => x.Id == vm.Id).Name);
        Assert.Equal(vm.Id, context.ProxmoxVmInfo.Single(x => x.Id == 101).VmId);
        Assert.Equal(rival.Id, context.ProxmoxVmInfo.Single(x => x.Id == 102).VmId);
    }

    /// <summary>
    /// Commits <paramref name="rival"/> from another context when the service's save begins, once: the
    /// moment a concurrent request could win.
    /// </summary>
    private void RegisterWhenTheServiceSaves(VmEntity rival)
    {
        var registered = false;

        Db.SavingChanges += (_, _) =>
        {
            if (registered)
                return;

            registered = true;
            using var other = NewContext();
            other.Vms.Add(rival);
            other.SaveChanges();
        };
    }

    private static ClaimsPrincipal Principal(Guid userId) =>
        new(new ClaimsIdentity([new Claim("sub", userId.ToString())], "test"));
}
