// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Infrastructure.Exceptions;
using Xunit;

namespace Player.Vm.Api.Tests;

// The CD-ROM drive definition is rebuilt from PVE's existing raw definition rather than composed from
// scratch, so these cases pin down which tokens survive that rewrite.
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
}
