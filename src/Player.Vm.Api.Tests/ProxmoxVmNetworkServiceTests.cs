// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Proxmox;
using Xunit;
using DomainVm = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

// CanMountIso, which the UI uses to decide whether to offer the mount control at all. It has to mean
// "a mount can succeed": Proxmox cannot hot-add an IDE drive, so a QEMU Vm built without a CD/DVD
// drive can never accept one, and offering the control there only produces a 400 once the user has
// already picked a file.
public class ProxmoxVmNetworkServiceTests
{
    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static DomainVm Vm(ProxmoxVmType type) => new()
    {
        Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Name = "vm",
        VmTeams = [],
        ProxmoxVmInfo = new ProxmoxVmInfo { Id = 100, Node = "pve1", Type = type }
    };

    private static IIsoProvider IsoProvider(bool enabled)
    {
        var provider = Substitute.For<IIsoProvider>();
        provider.ProviderType.Returns(VmType.Proxmox);
        provider.Enabled.Returns(enabled);
        return provider;
    }

    private static async Task<bool> CanMountIso(
        ProxmoxVmType type,
        bool hasCdromDrive,
        bool isoProviderEnabled = true)
    {
        var vm = Vm(type);

        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetVmConfigSummary(vm.ProxmoxVmInfo, Arg.Any<CancellationToken>())
            .Returns(new ProxmoxVmConfigSummary(new Dictionary<string, string>(), hasCdromDrive));

        var service = new ProxmoxVmNetworkService(
            Substitute.For<IViewService>(),
            Substitute.For<INetworkService>(),
            proxmox,
            new ProxmoxOptions { Host = "pve.example.test" },
            [IsoProvider(isoProviderEnabled)]);

        var response = await service.ToResponse(
            vm,
            new ProxmoxNetworkPermissions(ViewId, new EffectiveNetworkPermission()),
            CancellationToken.None);

        return response.CanMountIso;
    }

    [Fact]
    public async Task CanMountIso_WhenTheVmIsQemuWithADrive()
    {
        Assert.True(await CanMountIso(ProxmoxVmType.QEMU, hasCdromDrive: true));
    }

    // The case the drive check was added for: a QEMU Vm whose CD/DVD drive was never defined, or was
    // removed. Nothing can hot-add one, so the control has to be withheld rather than fail on use.
    [Fact]
    public async Task CannotMountIso_WhenTheQemuVmHasNoCdromDrive()
    {
        Assert.False(await CanMountIso(ProxmoxVmType.QEMU, hasCdromDrive: false));
    }

    // A container has no optical drive at all, so the config read reports none either.
    [Fact]
    public async Task CannotMountIso_WhenTheVmIsALxcContainer()
    {
        Assert.False(await CanMountIso(ProxmoxVmType.LXC, hasCdromDrive: false));
    }

    // Belt and braces: even if a container somehow reported a drive, LXC is not mountable.
    [Fact]
    public async Task CannotMountIso_ForALxcContainerEvenIfADriveIsReported()
    {
        Assert.False(await CanMountIso(ProxmoxVmType.LXC, hasCdromDrive: true));
    }

    // A deployment that has not configured Proxmox ISO storage offers no mount control anywhere, drive
    // or no drive - and the answer comes from the provider itself, so it cannot drift from what an
    // upload would actually do.
    [Fact]
    public async Task CannotMountIso_WhenTheProxmoxIsoProviderIsDisabled()
    {
        Assert.False(await CanMountIso(ProxmoxVmType.QEMU, hasCdromDrive: true, isoProviderEnabled: false));
    }
}
