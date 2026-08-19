// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Files;
using Xunit;

namespace Player.Vm.Api.Tests;

// Covers IsoService.SummarizeFanOut, which turns each provider's outcome into the message the uploader
// sees. The point of these is the attribution: a mixed vSphere + Proxmox install used to report
// "failed on 1 of 1 hosts" with no way to tell which hypervisor was at fault.
public class IsoServiceFanOutTests
{
    private const string Suffix = "Try again, or contact an administrator if the issue persists.";

    private static IsoService.ProviderOutcome Succeeded(VmType provider, int hosts = 1) =>
        new(provider, false, 0, hosts);

    // A provider whose write threw reports no host numbers: only it knows how many targets it would
    // have had, and the storage-backed modes have none to report at all.
    private static IsoService.ProviderOutcome Threw(VmType provider) =>
        new(provider, true, 0, 0);

    private static IsoService.ProviderOutcome PartiallyFailed(VmType provider, int failed, int total) =>
        new(provider, false, failed, total);

    private static IsoUploadResult Upload(params IsoService.ProviderOutcome[] outcomes) =>
        IsoService.SummarizeFanOut(outcomes, "upload", "uploaded");

    [Fact]
    public void EverythingSucceeded_SaysSoAndNamesNoProvider()
    {
        var result = Upload(Succeeded(VmType.Vsphere, 3), Succeeded(VmType.Proxmox));

        Assert.Equal("ISO was uploaded", result.Message);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(4, result.TotalHostCount);
        Assert.False(result.PartialFailure);
    }

    // The case that prompted this: with both hypervisors enabled and one of them down, the uploader has
    // to be told which one, because re-uploading is what heals it.
    [Fact]
    public void OneOfTwoProvidersFailed_NamesOnlyThatProvider()
    {
        var result = Upload(Succeeded(VmType.Vsphere), Threw(VmType.Proxmox));

        Assert.Equal($"ISO uploaded, but failed on Proxmox. {Suffix}", result.Message);
        Assert.True(result.PartialFailure);
    }

    // The whole reason PartialFailure exists rather than being derived from FailedHostCount: a provider
    // that threw invents no host numbers, and the storage-backed write modes report none either, so a
    // real failure can leave both counts at zero.
    [Fact]
    public void ProviderThatThrew_InflatesNoHostCounts()
    {
        var result = Upload(Succeeded(VmType.Vsphere, 3), Threw(VmType.Proxmox));

        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(3, result.TotalHostCount);
        Assert.True(result.PartialFailure);
    }

    // A provider that reports no per-host tally gets no host clause: "Proxmox (1 of 1 hosts)" says
    // nothing the provider name does not already say.
    [Fact]
    public void ProviderWithNoHostTally_CarriesNoHostClause()
    {
        var result = Upload(Succeeded(VmType.Vsphere), Threw(VmType.Proxmox));

        Assert.DoesNotContain("hosts", result.Message);
    }

    // vSphere's datastore mode writes to every connected vCenter, so it can fail on some and not others -
    // and then the tally is the useful part, because the upload did partly land.
    [Fact]
    public void ProviderThatFailedOnSomeOfItsHosts_ReportsTheTally()
    {
        var result = Upload(PartiallyFailed(VmType.Vsphere, 1, 3), Succeeded(VmType.Proxmox));

        Assert.Equal($"ISO uploaded, but failed on Vsphere (1 of 3 hosts). {Suffix}", result.Message);
        Assert.Equal(1, result.FailedHostCount);
        Assert.Equal(4, result.TotalHostCount);

        // It did not fail outright, but the file is now missing from part of that hypervisor, which is
        // the same "re-upload to heal it" state as a provider that failed entirely.
        Assert.True(result.PartialFailure);
    }

    [Fact]
    public void TwoFailingProviders_AreBothNamed()
    {
        var result = Upload(PartiallyFailed(VmType.Vsphere, 2, 3), Threw(VmType.Proxmox));

        Assert.Equal($"ISO uploaded, but failed on Vsphere (2 of 3 hosts) and Proxmox. {Suffix}", result.Message);
        Assert.True(result.PartialFailure);
    }

    // Nothing landed anywhere, so there is no partial success to report - it throws, and the message
    // still names what failed rather than the old vacuous "all hypervisors" (which read oddly when
    // there was only one).
    [Fact]
    public void EveryProviderFailed_ThrowsNamingThemAll()
    {
        var ex = Assert.Throws<Exception>(() => Upload(Threw(VmType.Vsphere), Threw(VmType.Proxmox)));

        Assert.Equal($"ISO upload failed on Vsphere and Proxmox. {Suffix}", ex.Message);
    }

    [Fact]
    public void TheOnlyProviderFailed_ThrowsNamingIt()
    {
        var ex = Assert.Throws<Exception>(() => Upload(Threw(VmType.Proxmox)));

        Assert.Equal($"ISO upload failed on Proxmox. {Suffix}", ex.Message);
    }

    // One provider down and another only partly failing is still a partial success, not a total one:
    // some of vSphere's hosts took the file, so it must not throw.
    [Fact]
    public void OneProviderThrewAndAnotherPartlyFailed_DoesNotThrow()
    {
        var result = Upload(Threw(VmType.Proxmox), PartiallyFailed(VmType.Vsphere, 1, 3));

        Assert.True(result.PartialFailure);
        Assert.Contains("Proxmox", result.Message);
        Assert.Contains("Vsphere (1 of 3 hosts)", result.Message);
    }

    // Delete borrows the same reduction, so only the verb changes.
    [Fact]
    public void DeleteUsesItsOwnVerbs()
    {
        var outcomes = new[] { Succeeded(VmType.Vsphere), Threw(VmType.Proxmox) };

        var result = IsoService.SummarizeFanOut(outcomes, "delete", "deleted");

        Assert.Equal($"ISO deleted, but failed on Proxmox. {Suffix}", result.Message);
        Assert.Equal($"ISO delete failed on Proxmox. {Suffix}",
            Assert.Throws<Exception>(() => IsoService.SummarizeFanOut(
                new[] { Threw(VmType.Proxmox) }, "delete", "deleted")).Message);
    }

    // The storage-backed write modes report no per-host counts at all (a share is not a host), so a
    // fully successful upload can legitimately total zero hosts. That must still read as success.
    [Fact]
    public void ProvidersThatReportNoHostCounts_StillSucceed()
    {
        var result = Upload(Succeeded(VmType.Vsphere, 0), Succeeded(VmType.Proxmox, 0));

        Assert.Equal("ISO was uploaded", result.Message);
        Assert.Equal(0, result.TotalHostCount);
        Assert.False(result.PartialFailure);
    }
}
