// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files.Providers;
using Xunit;

namespace Player.Vm.Api.Tests;

// Whether the provider considers itself configured, and the one listing rule that depends on live
// connectivity. Both exist because a Proxmox-only install was charging a phantom failed host to a
// vSphere that was never set up, reporting "failed on 1 of 1 hosts" on an upload that fully succeeded.
public class VsphereIsoProviderTests
{
    private static VsphereIsoProvider Provider(
        VsphereOptions vsphereOptions,
        IVsphereService vsphereService = null) =>
        new(vsphereService, vsphereOptions);

    private static VsphereOptions WithHosts(params VsphereHost[] hosts) => new() { Hosts = hosts };

    private static VsphereHost GoodHost() => new() { Enabled = true, Address = "vcenter.example.test" };

    // One usable host plus the share this provider writes to - the default write mode.
    private static VsphereOptions NfsOptions(string isoRoot) =>
        new() { Hosts = [GoodHost()], IsoRoot = isoRoot };

    // One usable host, writing through vCenter's HTTP file API instead of a share.
    private static VsphereOptions ApiOptions() =>
        new() { Hosts = [GoodHost()], IsoUploadViaApi = true };

    [Fact]
    public void Provider_IdentifiesItsHypervisorTypeAndNothingElse()
    {
        // The type is all the contract exposes. There was never a single address to report here anyway -
        // an upload fans out across every connected vCenter - and a vCenter address is privileged
        // deployment detail that belongs in the server logs.
        Assert.Equal(VmType.Vsphere, Provider(WithHosts(GoodHost())).ProviderType);
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
    public void RequiresStagedFile_TracksTheWriteMode(bool isoUploadViaApi, bool expected)
    {
        var options = WithHosts(GoodHost());
        options.IsoUploadViaApi = isoUploadViaApi;

        Assert.Equal(expected, Provider(options).RequiresStagedFile);
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

        var provider = Provider(WithHosts(GoodHost()), vsphere);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ListAsync(null, CancellationToken.None));

        // Never asked for a listing it could not trust.
        await vsphere.DidNotReceive().ListIsos(Arg.Any<Guid?>());
    }

    // ---- ResolveMountTarget: the whole authorization boundary for a vSphere mount ----
    //
    // A datastore path names any file the datastore will serve, so the decoder is what stops a
    // submitted string from reaching the cdrom backing. Anything it accepts is rebuilt from the
    // decoded parts; anything else is null, which IsoService turns into a 403.

    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ScopeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static VsphereHost IsoHost(string baseFolder = "player") =>
        new() { Enabled = true, Address = "vcenter.example.test", DsName = "ds1", BaseFolder = baseFolder };

    private static string Canonical(string baseFolder = "player", string filename = "tools.iso") =>
        baseFolder.Length == 0
            ? $"[ds1] {ViewId}/{ScopeId}/{filename}"
            : $"[ds1] {baseFolder}/{ViewId}/{ScopeId}/{filename}";

    [Fact]
    public void ResolveMountTarget_DecodesAListingPathAndRebuildsIt()
    {
        var target = VsphereIsoProvider.ResolveMountTarget(IsoHost(), Canonical());

        Assert.NotNull(target);
        Assert.Equal(ViewId, target.ViewId);
        Assert.Equal(ScopeId, target.ScopeId);
        Assert.Equal("tools.iso", target.FileName);
        Assert.Equal(Canonical(), target.MountValue);
    }

    // A nested BaseFolder is one more fixed prefix, and no BaseFolder at all means the View id is the
    // first segment - both have to rebuild through the same layout helper the writer uses.
    [Theory]
    [InlineData("player")]
    [InlineData("isos/player")]
    [InlineData("")]
    public void ResolveMountTarget_HandlesEveryBaseFolderShape(string baseFolder)
    {
        var value = Canonical(baseFolder);

        var target = VsphereIsoProvider.ResolveMountTarget(IsoHost(baseFolder), value);

        Assert.Equal(value, target.MountValue);
    }

    // A view-scoped ISO lives in a folder named for the View twice; the mount rule skips the team check
    // for those, so the decoder must report it rather than fold it away.
    [Fact]
    public void ResolveMountTarget_ReportsAViewScopedIsoAsScopedToItsView()
    {
        var target = VsphereIsoProvider.ResolveMountTarget(
            IsoHost(), $"[ds1] player/{ViewId}/{ViewId}/tools.iso");

        Assert.Equal(ViewId, target.ViewId);
        Assert.Equal(ViewId, target.ScopeId);
    }

    [Fact]
    public void ResolveMountTarget_RejectsEverythingWhenTheVmsHostIsUnknown()
    {
        // No host resolved for the VM (unknown to vSphere, or no DsName configured) means nothing can be
        // authorized - not that the path passes unchecked.
        Assert.Null(VsphereIsoProvider.ResolveMountTarget(null, Canonical()));
        Assert.Null(VsphereIsoProvider.ResolveMountTarget(
            new VsphereHost { Enabled = true, Address = "vcenter.example.test", BaseFolder = "player" },
            Canonical()));
    }

    [Theory]
    // Another datastore on the same host, which is the one part of the path a caller can retarget.
    [InlineData("[ds2] player/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/tools.iso")]
    // A datastore whose name merely starts the same.
    [InlineData("[ds10] player/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/tools.iso")]
    [InlineData("[] player/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/tools.iso")]
    // Somewhere else on our own datastore.
    [InlineData("[ds1] templates/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/tools.iso")]
    [InlineData("[ds1] some-vm/some-vm.vmdk")]
    // Traversal, both out of the base folder and out of a scope folder.
    [InlineData("[ds1] player/../../etc/passwd")]
    [InlineData("[ds1] player/11111111-1111-1111-1111-111111111111/../22222222-2222-2222-2222-222222222222/tools.iso")]
    [InlineData("[ds1] player/11111111-1111-1111-1111-111111111111/./tools.iso")]
    // A missing segment (the View folder itself) and an extra one (a subfolder of a scope).
    [InlineData("[ds1] player/11111111-1111-1111-1111-111111111111/tools.iso")]
    [InlineData("[ds1] player/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/sub/tools.iso")]
    // An empty segment would otherwise satisfy the segment count with a folder that is not there.
    [InlineData("[ds1] player//11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/tools.iso")]
    // Neither scope segment may be anything but a GUID.
    [InlineData("[ds1] player/not-a-guid/22222222-2222-2222-2222-222222222222/tools.iso")]
    [InlineData("[ds1] player/11111111-1111-1111-1111-111111111111/not-a-guid/tools.iso")]
    // Not an ISO at all - a disk parked in an ISO folder must not become a readable CD.
    [InlineData("[ds1] player/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/disk.vmdk")]
    // The folder, with vSphere's own trailing slash, rather than a file in it.
    [InlineData("[ds1] player/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/")]
    // No datastore prefix at all.
    [InlineData("player/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/tools.iso")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ResolveMountTarget_RejectsAnythingItDidNotIssue(string mountValue)
    {
        Assert.Null(VsphereIsoProvider.ResolveMountTarget(IsoHost(), mountValue));
    }

    // ---- The two write modes ----

    private static string TempIsoRoot() =>
        Path.Combine(Path.GetTempPath(), "player-iso-tests", Guid.NewGuid().ToString());

    private static IsoUploadRequest Request(
        string stagedFilePath, string fileName = "tools.iso", params string[] scopeIds) =>
        new(ViewId, scopeIds.Length == 0 ? [ScopeId.ToString()] : scopeIds, fileName, stagedFilePath, null);

    private static string StagedFile(string contents = "iso-bytes")
    {
        var path = Path.Combine(Path.GetTempPath(), "player-iso-tests", Guid.NewGuid() + ".iso");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public async Task Upload_InNfsMode_WritesOneCopyPerScopeFolderUnderIsoRoot()
    {
        var isoRoot = TempIsoRoot();
        var otherScope = Guid.NewGuid();
        var staged = StagedFile();

        try
        {
            var provider = Provider(NfsOptions(isoRoot));

            var result = await provider.UploadAsync(
                Request(staged, "tools.iso", ScopeId.ToString(), otherScope.ToString()),
                CancellationToken.None);

            foreach (var scope in new[] { ScopeId, otherScope })
            {
                var written = Path.Combine(isoRoot, ViewId.ToString(), scope.ToString(), "tools.iso");
                Assert.True(File.Exists(written));
                Assert.Equal("iso-bytes", File.ReadAllText(written));
            }

            // A share is not a host, so this mode reports no per-host tally - which is what it has
            // always put on the wire.
            Assert.Equal(0, result.TotalHostCount);
            Assert.Equal(0, result.FailedHostCount);
        }
        finally
        {
            Directory.Delete(isoRoot, true);
            File.Delete(staged);
        }
    }

    // Datastore mode is the one that has hosts to count, and it fans out per scope inside
    // VsphereService - so the provider's job is to sum what each scope reported.
    [Fact]
    public async Task Upload_InDatastoreMode_SumsThePerHostCountsAcrossScopes()
    {
        var otherScope = Guid.NewGuid();
        var vsphere = Substitute.For<IVsphereService>();
        vsphere.UploadIso(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new IsoOperationOutcome { FailedHostCount = 1, TotalHostCount = 3 });

        var provider = Provider(ApiOptions(), vsphere);

        var result = await provider.UploadAsync(
            Request("/tmp/staged.iso", "tools.iso", ScopeId.ToString(), otherScope.ToString()),
            CancellationToken.None);

        await vsphere.Received(1).UploadIso(
            ViewId.ToString(), ScopeId.ToString(), "tools.iso", "/tmp/staged.iso");
        await vsphere.Received(1).UploadIso(
            ViewId.ToString(), otherScope.ToString(), "tools.iso", "/tmp/staged.iso");

        Assert.Equal(2, result.FailedHostCount);
        Assert.Equal(6, result.TotalHostCount);
    }

    [Fact]
    public async Task Upload_InDatastoreMode_RefusesToUploadWithoutAStagedFile()
    {
        var vsphere = Substitute.For<IVsphereService>();

        var provider = Provider(ApiOptions(), vsphere);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.UploadAsync(Request(null), CancellationToken.None));

        await vsphere.DidNotReceive().UploadIso(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // The legacy-name case, and the reason IsoService stopped folding filenames on delete: this
    // provider's stored names are whatever the filesystem accepted, spaces and all, so a delete has to
    // reach exactly the name the listing reported.
    [Fact]
    public async Task Delete_InNfsMode_RemovesTheFileUnderTheNameItWasGivenVerbatim()
    {
        var isoRoot = TempIsoRoot();
        var folder = Path.Combine(isoRoot, ViewId.ToString(), ScopeId.ToString());
        Directory.CreateDirectory(folder);
        var stored = Path.Combine(folder, "Win 10 (x64).iso");
        File.WriteAllText(stored, "iso-bytes");

        try
        {
            var provider = Provider(NfsOptions(isoRoot));

            await provider.DeleteAsync(
                ViewId, ScopeId.ToString(), "Win 10 (x64).iso", CancellationToken.None);

            Assert.False(File.Exists(stored));
        }
        finally
        {
            Directory.Delete(isoRoot, true);
        }
    }

    [Fact]
    public async Task Delete_InNfsMode_TreatsAMissingFileAsSuccess()
    {
        var provider = Provider(NfsOptions(TempIsoRoot()));

        await provider.DeleteAsync(ViewId, ScopeId.ToString(), "gone.iso", CancellationToken.None);
    }

    // The un-migrated deployment: the vSphere upload settings moved out of IsoUpload, and a legacy key
    // is ignored rather than mapped forward, so the first upload has to say which key is missing. Left
    // blank it would otherwise Path.Combine into a relative directory next to the process and appear to
    // succeed.
    [Fact]
    public async Task Upload_InNfsMode_WithNoIsoRootConfigured_SaysWhichSettingIsMissing()
    {
        var provider = Provider(WithHosts(GoodHost()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.UploadAsync(Request(StagedFile()), CancellationToken.None));

        Assert.Contains("Vsphere:IsoRoot", ex.Message);
    }

    [Fact]
    public async Task Delete_InNfsMode_WithNoIsoRootConfigured_SaysWhichSettingIsMissing()
    {
        var provider = Provider(WithHosts(GoodHost()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.DeleteAsync(ViewId, ScopeId.ToString(), "gone.iso", CancellationToken.None));

        Assert.Contains("Vsphere:IsoRoot", ex.Message);
    }

    [Fact]
    public async Task Delete_InDatastoreMode_PassesTheNameThroughVerbatimAndReportsItsHostCounts()
    {
        var vsphere = Substitute.For<IVsphereService>();
        vsphere.DeleteIso(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new IsoOperationOutcome { FailedHostCount = 1, TotalHostCount = 2 });

        var provider = Provider(ApiOptions(), vsphere);

        var result = await provider.DeleteAsync(
            ViewId, ScopeId.ToString(), "Win 10 (x64).iso", CancellationToken.None);

        await vsphere.Received(1).DeleteIso(
            ViewId.ToString(), ScopeId.ToString(), "Win 10 (x64).iso");

        Assert.Equal(1, result.FailedHostCount);
        Assert.Equal(2, result.TotalHostCount);
    }
}
