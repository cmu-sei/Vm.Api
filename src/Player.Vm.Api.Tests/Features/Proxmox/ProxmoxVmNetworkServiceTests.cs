// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Features.Networks;
using Player.Vm.Api.Features.Proxmox;
using DomainVm = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests.Features.Proxmox;

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
    private static async Task<(ProxmoxVirtualMachine Response, IProxmoxService Proxmox)> Respond(
        ProxmoxVmConfigSummary summary, bool pingAnswers = true)
    {
        var vm = Vm(ProxmoxVmType.QEMU);

        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetVmConfigSummary(vm.ProxmoxVmInfo, Arg.Any<CancellationToken>()).Returns(summary);
        proxmox.IsGuestAgentRunning(vm.ProxmoxVmInfo).Returns(pingAnswers);

        var service = new ProxmoxVmNetworkService(
            Substitute.For<IViewService>(),
            Substitute.For<INetworkService>(),
            proxmox,
            new ProxmoxOptions { Host = "pve.example.test" },
            [IsoProvider(true)]);

        var response = await service.ToResponse(
            vm,
            new ProxmoxNetworkPermissions(ViewId, new EffectiveNetworkPermission()),
            CancellationToken.None);

        return (response, proxmox);
    }

    // GuestAgentRunning is the ping's answer where the config turns the agent on.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GuestAgentRunning_IsWhatThePingSays_WhenTheAgentIsEnabled(bool pingAnswers)
    {
        var (response, _) = await Respond(
            new ProxmoxVmConfigSummary([], false) { AgentEnabled = true }, pingAnswers);

        Assert.Equal(pingAnswers, response.GuestAgentRunning);
    }

    // Where the config does not enable the agent the answer can only be false, so there is no ping - which
    // would otherwise be paid on every Get, network change and ISO mount response.
    [Fact]
    public async Task GuestAgentRunning_IsFalseWithoutAPing_WhenTheAgentIsNotEnabled()
    {
        var (response, proxmox) = await Respond(new ProxmoxVmConfigSummary([], false) { AgentEnabled = false });

        Assert.False(response.GuestAgentRunning);
        await proxmox.DidNotReceive().IsGuestAgentRunning(Arg.Any<ProxmoxVmInfo>());
    }

    // The snapshot fields come from the same live config read.
    [Theory]
    [InlineData("before-patching", true)]
    [InlineData(null, false)]
    public async Task HasSnapshotAndCurrentSnapshot_ComeFromTheConfigsParent(string parent, bool hasSnapshot)
    {
        var (response, _) = await Respond(new ProxmoxVmConfigSummary([], false) { CurrentSnapshot = parent });

        Assert.Equal(hasSnapshot, response.HasSnapshot);
        Assert.Equal(parent, response.CurrentSnapshot);
    }

    // The VNC clipboard flag is passed through from the same live config read.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VncClipboardEnabled_ComesFromTheConfigSummary(bool enabled)
    {
        var (response, _) = await Respond(new ProxmoxVmConfigSummary([], false) { VncClipboardEnabled = enabled });

        Assert.Equal(enabled, response.VncClipboardEnabled);
    }

    // The upload limit is the one ProxmoxService enforces, so the console can refuse a file the API would
    // refuse, and a deployment that configures a lower FileUploadMaxBytes is reflected without a UI change.
    [Fact]
    public async Task FileUploadMaxBytes_IsTheLimitTheUploadEnforces()
    {
        var vm = Vm(ProxmoxVmType.QEMU);
        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetVmConfigSummary(vm.ProxmoxVmInfo, Arg.Any<CancellationToken>())
            .Returns(new ProxmoxVmConfigSummary([], false));
        proxmox.UploadLimitBytes.Returns(2048);

        var service = new ProxmoxVmNetworkService(
            Substitute.For<IViewService>(),
            Substitute.For<INetworkService>(),
            proxmox,
            new ProxmoxOptions { Host = "pve.example.test" },
            [IsoProvider(true)]);

        var response = await service.ToResponse(
            vm,
            new ProxmoxNetworkPermissions(ViewId, new EffectiveNetworkPermission()),
            CancellationToken.None);

        Assert.Equal(2048, response.FileUploadMaxBytes);
    }
}
