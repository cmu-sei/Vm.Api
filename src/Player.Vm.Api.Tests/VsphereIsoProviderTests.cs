// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Infrastructure.Options;
using Xunit;

namespace Player.Vm.Api.Tests;

// Whether the provider considers itself configured, and the one listing rule that depends on live
// connectivity. Both exist because a Proxmox-only install was charging a phantom failed host to a
// vSphere that was never set up, reporting "failed on 1 of 1 hosts" on an upload that fully succeeded.
public class VsphereIsoProviderTests
{
    private static VsphereIsoProvider Provider(
        VsphereOptions vsphereOptions,
        IsoUploadOptions isoUploadOptions = null,
        IVsphereService vsphereService = null) =>
        new(vsphereService, isoUploadOptions ?? new IsoUploadOptions(), vsphereOptions);

    private static VsphereOptions WithHosts(params VsphereHost[] hosts) => new() { Hosts = hosts };

    private static VsphereHost GoodHost() => new() { Enabled = true, Address = "vcenter.example.test" };

    [Fact]
    public void Provider_IdentifiesItselfAsVsphereWithNoInstance()
    {
        var provider = Provider(WithHosts(GoodHost()));

        Assert.Equal(VmType.Vsphere, provider.ProviderType);

        // Blank on purpose: an upload fans out across every connected vCenter, so no single address
        // describes it.
        Assert.Equal(string.Empty, provider.ProviderInstanceId);
    }

    [Fact]
    public void Enabled_WhenAHostIsConfigured()
    {
        Assert.True(Provider(WithHosts(GoodHost())).Enabled);
    }

    // The whole point of the fix: with nothing configured the provider is invisible, so a Proxmox-only
    // install stops being told its successful uploads failed on a vSphere host that does not exist.
    // Includes the shipped appsettings.json default, which is Enabled:false with a blank Address.
    [Theory]
    [InlineData(false, "vcenter.example.test")]  // host explicitly disabled
    [InlineData(true, "")]                       // no address to connect to
    [InlineData(true, "   ")]                    // whitespace is not an address
    [InlineData(true, null)]
    [InlineData(false, "")]                      // the shipped default
    public void Disabled_WhenTheOnlyHostIsNotUsable(bool enabled, string address)
    {
        var options = WithHosts(new VsphereHost { Enabled = enabled, Address = address });

        Assert.False(Provider(options).Enabled);
    }

    [Fact]
    public void Disabled_WhenThereAreNoHostsAtAll()
    {
        Assert.False(Provider(WithHosts()).Enabled);
        Assert.False(Provider(new VsphereOptions()).Enabled);       // Hosts null - no Vsphere section
    }

    // One usable host among unusable ones is still a configured vSphere.
    [Fact]
    public void Enabled_WhenAtLeastOneHostIsUsable()
    {
        var options = WithHosts(
            new VsphereHost { Enabled = false, Address = "retired.example.test" },
            new VsphereHost { Enabled = true, Address = "" },
            GoodHost());

        Assert.True(Provider(options).Enabled);
    }

    // Datastore mode streams to each vCenter and so needs a re-readable local file; the NFS path writes
    // to a share once and can consume the request body as it arrives.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RequiresStagedFile_TracksTheWriteMode(bool uploadToDatastore, bool expected)
    {
        var provider = Provider(
            WithHosts(GoodHost()),
            new IsoUploadOptions { UploadToDatastore = uploadToDatastore });

        Assert.Equal(expected, provider.RequiresStagedFile);
    }

    [Fact]
    public void TargetCount_IsTheConnectedHostCountInDatastoreModeAndOneOverNfs()
    {
        var vsphere = Substitute.For<IVsphereService>();
        vsphere.GetEnabledConnectionCount().Returns(3);

        Assert.Equal(3, Provider(WithHosts(GoodHost()),
            new IsoUploadOptions { UploadToDatastore = true }, vsphere).TargetCount);

        // An NFS write goes to a share, not to individual hosts, so it is one target however many
        // vCenters are connected.
        Assert.Equal(1, Provider(WithHosts(GoodHost()),
            new IsoUploadOptions { UploadToDatastore = false }, vsphere).TargetCount);
    }

    [Fact]
    public void NormalizeFilename_IsTheIdentity()
    {
        // Folding names here would silently rename files for vSphere-only installs, which have no
        // reason to accept a narrower character set than the filesystem does.
        Assert.Equal("Win 10 (x64).iso", Provider(WithHosts(GoodHost())).NormalizeFilename("Win 10 (x64).iso"));
    }

    [Fact]
    public void ValidateFilename_AcceptsAnythingTheFilesystemWould()
    {
        // Folder scoping imposes no naming constraints of its own, so there is nothing to enforce.
        Provider(WithHosts(GoodHost())).ValidateFilename(
            Guid.NewGuid(), Guid.NewGuid().ToString(), "Win 10 (x64)#weird__name.iso");
    }

    // A configured vCenter that is currently unreachable must fail its listing rather than return an
    // empty one. VsphereService.ListIsos logs and returns empty in that case, which the merge would
    // read as "this file is missing on vSphere" and badge every other provider's row incomplete.
    [Fact]
    public async Task ListAsync_ThrowsRatherThanReportingAnEmptyListing_WhenNothingIsConnected()
    {
        var vsphere = Substitute.For<IVsphereService>();
        vsphere.GetEnabledConnectionCount().Returns(0);

        var provider = Provider(WithHosts(GoodHost()), null, vsphere);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ListAsync(null, CancellationToken.None));

        // Never asked for a listing it could not trust.
        await vsphere.DidNotReceive().ListIsos(Arg.Any<Guid?>());
    }
}
