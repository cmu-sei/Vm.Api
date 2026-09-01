// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The request <c>ProxmoxService.MountIso</c> actually sends to load an ISO, driven through a
/// substituted transport so no Proxmox cluster is involved.
/// </summary>
/// <remarks>
/// <para>
/// The three decisions a mount makes - which drive to target, how to rewrite its definition, and how to
/// read a drive key - are covered exhaustively as statics by <c>ProxmoxDriveDefinitionTests</c>, and are
/// deliberately not restated here. What that class cannot see, and this one exists for, is the wiring:
/// that <c>MountIso</c> puts those three together into one config update addressed to the right node,
/// keyed on the right bus, carrying the right definition.
/// </para>
/// <para>
/// The load-bearing case is the per-bus dispatch. PVE documents its <c>cdrom</c> update parameter as an
/// alias for <c>-ide2</c>, so a mount written through it would write <c>ide2</c> on a Vm whose optical
/// drive is <c>sata1</c> - adding a second drive rather than loading the one that is there, silently and
/// only on the Vms that were not built to the Proxmox convention. Nothing but the sata and scsi rows of
/// <see cref="MountIso_WritesTheKeyOfTheDrivesOwnBus"/> would notice that.
/// </para>
/// </remarks>
public class ProxmoxServiceIsoMountTests
{
    private const int Vmid = 100;

    private const string Iso = "nfs:iso/thing.iso";

    /// <summary>An ordinary virtual disk, to show a config with drives in it but no optical drive.</summary>
    private const string Disk = @"""scsi0"":""local-lvm:vm-100-disk-0,size=32G""";

    private const string Nic = @"""net0"":""virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr1""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Refusals

    // A container has no CD-ROM to swap, so this is a property of the request rather than a server
    // fault - hence BadRequest and not EnsureQemu's InvalidOperationException, which is what every other
    // QEMU-only path on this service throws. Refused before anything is asked of Proxmox, so a caller
    // that gets this back knows nothing was attempted.
    [Fact]
    public async Task MountIso_OnAContainer_IsRefusedAsABadRequestBeforeProxmoxIsAskedAnything()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => cluster.Service().MountIso(info, Iso, Ct));

        Assert.Equal("Mounting an ISO is only supported on QEMU virtual machines.", ex.Message);
        Assert.Empty(cluster.Http.Sent);
    }

    // A vmid the cluster has never heard of is a server-side inconsistency - the Vm exists in this
    // application's database but not in Proxmox - so it is not a BadRequest.
    [Fact]
    public async Task MountIso_WhenProxmoxHasNeverHeardOfTheVmid_Throws()
    {
        var cluster = new FakeProxmoxCluster();

        var info = new ProxmoxVmInfo { Id = Vmid, Node = FakeProxmoxCluster.DefaultNode, Type = ProxmoxVmType.QEMU };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.Service().MountIso(info, Iso, Ct));

        Assert.Equal($"Could not find vmid {Vmid} in Proxmox", ex.Message);

        // The config was never read, so the refusal came from the resource lookup and not from a
        // failure further in.
        Assert.Equal([FakeProxmoxCluster.ClusterResources], cluster.Http.Paths);
    }

    // A Vm with no optical drive is refused rather than having one added, because QEMU cannot hot-add an
    // IDE drive: adding ide2 to a running Vm lands in the pending config, so the mount would appear to
    // do nothing at all until the next power cycle. The two configs are the two shapes that reach this -
    // a Vm with real disks and no cdrom, and a Vm whose config PVE reports as empty.
    [Theory]
    [InlineData("{" + Disk + "," + Nic + "}")]
    [InlineData("{}")]
    public async Task MountIso_WhenTheVmHasNoOpticalDrive_IsRefusedWithoutWritingTheConfig(string configJson)
    {
        var (cluster, info, path) = Vm(configJson);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => cluster.Service().MountIso(info, Iso, Ct));

        Assert.Equal(
            $"vmid {Vmid} has no CD/DVD drive. Add one to the VM in Proxmox before mounting an ISO.",
            ex.Message);

        // Nothing stubs the POST, so a write would have thrown from the transport instead - this pins
        // that the config was read and then left alone.
        Assert.Equal([FakeProxmoxCluster.ClusterResources, path], cluster.Http.Paths);
    }

    // A cdrom on a bus the config update has no dictionary for is refused rather than guessed at. The
    // alternative - falling back to ide2 - is the defect the per-bus dispatch exists to prevent.
    [Fact]
    public async Task MountIso_WhenTheOnlyDriveIsOnAnUnwritableBus_IsRefusedWithoutWritingTheConfig()
    {
        var (cluster, info, path) = Vm(@"{""virtio0"":""nfs:iso/old.iso,media=cdrom""}");

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => cluster.Service().MountIso(info, Iso, Ct));

        Assert.Equal(
            $"The CD/DVD drive 'virtio0' on vmid {Vmid} is on a bus that cannot be reconfigured.",
            ex.Message);
        Assert.Equal([FakeProxmoxCluster.ClusterResources, path], cluster.Http.Paths);
    }

    // A refused write is a failed mount, not a mount that reported success. Nothing above this reads the
    // result of the update, so if the throw were dropped the UI would show an ISO that is not inserted.
    [Fact]
    public async Task MountIso_WhenProxmoxRefusesTheConfigUpdate_Throws()
    {
        var (cluster, info, path) = Vm(@"{""ide2"":""none,media=cdrom""}");
        cluster.Rejects($"POST {path}", "unable to parse directory volume name 'thing.iso'");

        await Assert.ThrowsAnyAsync<Exception>(() => cluster.Service().MountIso(info, Iso, Ct));
    }

    #endregion

    #region The per-bus dispatch

    // The core of this class. Each bus is written under its own key, so the drive that is already on the
    // Vm is the drive that is loaded. Writing ide2 for a sata or scsi drive - which is what PVE's
    // ide2-only "cdrom" alias would do - would create a second, empty optical drive and leave the real
    // one untouched.
    [Theory]
    [InlineData("ide2")]
    [InlineData("sata1")]
    [InlineData("scsi3")]
    public async Task MountIso_WritesTheKeyOfTheDrivesOwnBus(string driveId)
    {
        var (cluster, info, path) = Vm($@"{{""{driveId}"":""none,media=cdrom"",{Disk},{Nic}}}");
        cluster.Accepts($"POST {path}");

        await cluster.Service().MountIso(info, Iso, Ct);

        Assert.Equal($@"{{""{driveId}"":""{Iso},media=cdrom""}}", cluster.Request(HttpMethod.Post, path).Body);
    }

    // The whole conversation a mount is, in order: resolve the Vm, read its config, write one key back.
    // Pinned as a sequence because the config read is what makes the write addressable, and because a
    // second read here would mean the config was being fetched twice per mount.
    [Fact]
    public async Task MountIso_ResolvesThenReadsTheConfigThenWritesItOnce()
    {
        var (cluster, info, path) = Vm(@"{""ide2"":""none,media=cdrom""}");
        cluster.Accepts($"POST {path}");

        await cluster.Service().MountIso(info, Iso, Ct);

        Assert.Equal([FakeProxmoxCluster.ClusterResources, path, path], cluster.Http.Paths);

        // current=1 on the read: the running config, so a pending change does not decide which drive is
        // targeted.
        Assert.Equal("?current=1", cluster.Request(HttpMethod.Get, path).Query);
    }

    // An empty drive is the common real case - a Vm built with an optical drive that has never had an
    // ISO in it - and loading it is the whole point of the feature.
    [Fact]
    public async Task MountIso_LoadsAnEmptyDrive()
    {
        var (cluster, info, path) = Vm(@"{""ide2"":""none,media=cdrom""}");
        cluster.Accepts($"POST {path}");

        await cluster.Service().MountIso(info, Iso, Ct);

        Assert.Equal($@"{{""ide2"":""{Iso},media=cdrom""}}", cluster.Request(HttpMethod.Post, path).Body);
    }

    // The definition is rebuilt from the one already on the drive, so an operator's flags survive a
    // mount. size= is the one token dropped: it describes the medium being replaced, and PVE re-derives
    // it for the new one.
    [Fact]
    public async Task MountIso_KeepsTheFlagsAlreadyOnTheDriveAndDropsPvesSizeToken()
    {
        var (cluster, info, path) = Vm(@"{""ide2"":""nfs:iso/old.iso,media=cdrom,size=700M,ro=1""}");
        cluster.Accepts($"POST {path}");

        await cluster.Service().MountIso(info, Iso, Ct);

        Assert.Equal(
            $@"{{""ide2"":""{Iso},media=cdrom,ro=1""}}", cluster.Request(HttpMethod.Post, path).Body);
    }

    // With more than one optical drive the choice has to be stable, or a second mount on the same Vm
    // would land in a different drive and leave the first ISO inserted. Both config orderings are here
    // because PVE reports the config as an object and nothing guarantees which key comes first.
    [Theory]
    [InlineData(@"{""ide2"":""none,media=cdrom"",""sata0"":""none,media=cdrom""}")]
    [InlineData(@"{""sata0"":""none,media=cdrom"",""ide2"":""none,media=cdrom""}")]
    public async Task MountIso_WithSeveralOpticalDrives_LoadsIde2WhateverOrderTheConfigListsThemIn(
        string configJson)
    {
        var (cluster, info, path) = Vm(configJson);
        cluster.Accepts($"POST {path}");

        await cluster.Service().MountIso(info, Iso, Ct);

        Assert.Equal($@"{{""ide2"":""{Iso},media=cdrom""}}", cluster.Request(HttpMethod.Post, path).Body);
    }

    // A cdrom drive is picked out from among the Vm's ordinary disks, which is the shape of every Vm that
    // actually boots something. The disks here are deliberately on buses a config update can write, and
    // one of them sorts ahead of the optical drive, so a mount that stopped telling a medium from a disk
    // would load the ISO over a system disk rather than merely failing to find a drive.
    [Fact]
    public async Task MountIso_PicksTheOpticalDriveOutFromAmongTheVmsOrdinaryDisks()
    {
        var (cluster, info, path) = Vm(
            $@"{{""ide0"":""local-lvm:vm-100-disk-0,size=32G"",""sata1"":""none,media=cdrom"",""virtio0"":""local-lvm:vm-100-disk-1,size=8G"",{Nic}}}");
        cluster.Accepts($"POST {path}");

        await cluster.Service().MountIso(info, Iso, Ct);

        Assert.Equal($@"{{""sata1"":""{Iso},media=cdrom""}}", cluster.Request(HttpMethod.Post, path).Body);
    }

    #endregion

    #region The migration refresh and the state poll

    // MountIso resolves the Vm before touching it, so a ProxmoxVmInfo whose node went stale on migration
    // is corrected and both halves of the mount are addressed to the node Proxmox currently reports. A
    // config update sent to the node a Vm has left is a 595 from PVE, not a redirect.
    [Fact]
    public async Task MountIso_AfterTheVmMigrated_ReadsAndWritesTheConfigOnTheNodeItIsNowOn()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Migrates(Vmid, "pve2");

        var path = FakeProxmoxCluster.VmPath("pve2", ProxmoxVmType.QEMU, Vmid, "/config");
        cluster.Answers($"GET {path}", @"{""ide2"":""none,media=cdrom""}");
        cluster.Accepts($"POST {path}");

        await cluster.Service().MountIso(info, Iso, Ct);

        // Nothing is stubbed on pve1, so a stale address would have thrown from the transport.
        Assert.Equal([FakeProxmoxCluster.ClusterResources, path, path], cluster.Http.Paths);
        Assert.Equal("pve2", info.Node);
    }

    // The state poller is nudged once the mount has been accepted, so the UI reflects the new medium
    // without waiting out the poll interval - the same courtesy every power command does.
    [Fact]
    public async Task MountIso_WhenTheMountSucceeds_AsksTheStatePollerToRunAgain()
    {
        var (cluster, info, path) = Vm(@"{""ide2"":""none,media=cdrom""}");
        cluster.Accepts($"POST {path}");

        await cluster.Service().MountIso(info, Iso, Ct);

        cluster.State.Received(1).CheckState();
    }

    // A refusal changed nothing, so there is no new state to go and look for. Pinned per refusal because
    // each one leaves the method by a different route.
    [Theory]
    [InlineData("{}")]
    [InlineData(@"{""virtio0"":""none,media=cdrom""}")]
    public async Task MountIso_WhenTheMountIsRefused_DoesNotNudgeTheStatePoller(string configJson)
    {
        var (cluster, info, _) = Vm(configJson);

        await Assert.ThrowsAsync<BadRequestException>(() => cluster.Service().MountIso(info, Iso, Ct));

        cluster.State.DidNotReceive().CheckState();
    }

    #endregion

    /// <summary>
    /// A QEMU Vm on the default node whose <c>?current=1</c> config Proxmox answers with the given
    /// entries, and the config route - which is the same path for the read and the write, so a test that
    /// wants the write refused simply does not stub the POST.
    /// </summary>
    private static (FakeProxmoxCluster Cluster, ProxmoxVmInfo Info, string ConfigPath) Vm(string configJson)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        var path = FakeProxmoxCluster.VmPath(info, "/config");

        cluster.Answers($"GET {path}", configJson);

        return (cluster, info, path);
    }
}
