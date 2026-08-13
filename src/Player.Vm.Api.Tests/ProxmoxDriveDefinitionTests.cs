// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Corsinvest.ProxmoxVE.Api.Shared.Models.Vm;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Infrastructure.Exceptions;
using Xunit;

namespace Player.Vm.Api.Tests;

// Everything an ISO mount decides about a Vm's optical drive without needing a cluster: which drive it
// targets, and how that drive's definition is rewritten. The definition is rebuilt from PVE's existing
// raw definition rather than composed from scratch, so these cases pin down which tokens survive.
public class ProxmoxDriveDefinitionTests
{
    private const string Volid = "nfs:iso/new.iso";

    [Fact]
    public void ReplaceCdromVolume_SwapsTheVolumeAndKeepsMediaCdrom()
    {
        Assert.Equal(
            $"{Volid},media=cdrom",
            ProxmoxService.ReplaceCdromVolume("nfs:iso/old.iso,media=cdrom", Volid));
    }

    // "none,media=cdrom" is what an empty drive looks like - the common case for a VM that has never had
    // an ISO mounted.
    [Fact]
    public void ReplaceCdromVolume_FillsAnEmptyDrive()
    {
        Assert.Equal(
            $"{Volid},media=cdrom",
            ProxmoxService.ReplaceCdromVolume("none,media=cdrom", Volid));
    }

    // PVE appends size= itself, and it describes the medium being replaced, so carrying it over to a
    // different ISO would be wrong. PVE re-derives it.
    [Fact]
    public void ReplaceCdromVolume_DropsPvesSizeToken()
    {
        Assert.Equal(
            $"{Volid},media=cdrom",
            ProxmoxService.ReplaceCdromVolume("nfs:iso/old.iso,media=cdrom,size=4G", Volid));
    }

    // Anything else on the drive is the operator's, not ours, and has to survive.
    [Fact]
    public void ReplaceCdromVolume_PreservesOtherFlags()
    {
        Assert.Equal(
            $"{Volid},media=cdrom,ro=1",
            ProxmoxService.ReplaceCdromVolume("nfs:iso/old.iso,media=cdrom,ro=1", Volid));
    }

    [Fact]
    public void ReplaceCdromVolume_AddsMediaCdromWhenAbsent()
    {
        Assert.Equal(
            $"{Volid},media=cdrom",
            ProxmoxService.ReplaceCdromVolume("nfs:iso/old.iso", Volid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReplaceCdromVolume_BuildsAMinimalDefinitionFromNothing(string rawDefinition)
    {
        Assert.Equal(
            $"{Volid},media=cdrom",
            ProxmoxService.ReplaceCdromVolume(rawDefinition, Volid));
    }

    [Theory]
    [InlineData("ide2", "ide", 2)]
    [InlineData("sata0", "sata", 0)]
    [InlineData("scsi1", "scsi", 1)]
    [InlineData("IDE2", "ide", 2)]
    public void ParseDriveId_SplitsBusFromIndex(string driveId, string expectedBus, int expectedIndex)
    {
        var (bus, index) = ProxmoxService.ParseDriveId(driveId);

        Assert.Equal(expectedBus, bus);
        Assert.Equal(expectedIndex, index);
    }

    [Theory]
    [InlineData("ide")]
    [InlineData("2")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseDriveId_RejectsAnythingElse(string driveId)
    {
        Assert.Throws<BadRequestException>(() => ProxmoxService.ParseDriveId(driveId));
    }

    // ---- ChooseCdromDrive: which of a Vm's optical drives a mount loads ----
    //
    // The choice is deliberately independent of what is currently in the drive, so mounting twice in a
    // row replaces one medium rather than filling a second drive.

    private static VmDisk Drive(string id, string rawDefinition = "none,media=cdrom") =>
        new() { Id = id, RawDefinition = rawDefinition };

    // The overwhelmingly common shape, and the Proxmox convention.
    [Fact]
    public void ChooseCdromDrive_TakesTheOnlyDrive_WhenItIsIde2()
    {
        Assert.Equal("ide2", ProxmoxService.ChooseCdromDrive([Drive("ide2")]).Id);
    }

    // A Vm built without ide2 still has to be mountable - which is why the update is dispatched on the
    // drive's real bus rather than through PVE's ide2-only "cdrom" parameter.
    [Fact]
    public void ChooseCdromDrive_TakesTheOnlyDrive_WhenThereIsNoIde2()
    {
        Assert.Equal("sata0", ProxmoxService.ChooseCdromDrive([Drive("sata0")]).Id);
    }

    [Theory]
    [InlineData("ide2", "sata0")]
    [InlineData("sata0", "ide2")]   // config order must not decide it
    [InlineData("ide2", "IDE2")]
    public void ChooseCdromDrive_PrefersIde2_WhenThereIsMoreThanOneDrive(string first, string second)
    {
        var chosen = ProxmoxService.ChooseCdromDrive([Drive(first), Drive(second)]);

        Assert.Equal("ide2", chosen.Id, ignoreCase: true);
    }

    // The stability rule, and the reason an empty drive is not preferred: if mounting picked whichever
    // drive was free, a second mount on the same Vm would land in a different drive and leave the first
    // ISO still inserted.
    [Fact]
    public void ChooseCdromDrive_StillPrefersAnOccupiedIde2_OverAnEmptyDrive()
    {
        var chosen = ProxmoxService.ChooseCdromDrive(
            [Drive("ide2", "nfs:iso/already-mounted.iso,media=cdrom"), Drive("sata0")]);

        Assert.Equal("ide2", chosen.Id);
    }

    // No ide2 to prefer, so the drive is resolved by key - a fixed answer for a given Vm, whatever order
    // PVE happens to report its drives in.
    [Theory]
    [InlineData("sata0", "scsi1")]
    [InlineData("scsi1", "sata0")]
    public void ChooseCdromDrive_WithNoIde2_PicksTheLowestKeyRatherThanTheFirstReported(
        string first, string second)
    {
        Assert.Equal("sata0", ProxmoxService.ChooseCdromDrive([Drive(first), Drive(second)]).Id);
    }
}
