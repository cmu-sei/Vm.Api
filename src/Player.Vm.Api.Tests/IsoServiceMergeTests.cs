// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Player.Api.Client;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Infrastructure.Options;
using Xunit;

namespace Player.Vm.Api.Tests;

// Covers IsoService.MergeListings, which is what the Files tab shows: one row per filename per scope,
// with MissingProviders recording the hypervisors that do not have the file. Plus the one behaviour of
// its caller that cannot be expressed in the pure merge: a listing where every provider failed.
public class IsoServiceMergeTests
{
    private static readonly Guid ScopeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ScopeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // Which provider a listing came from is carried by ProviderListing, not stamped on each entry -
    // a file row has no provider identity on it at all.
    private static IsoService.ProviderListing Listing(
        VmType provider,
        params (Guid ScopeId, string[] Filenames)[] scopes)
    {
        return new IsoService.ProviderListing(provider, scopes.ToDictionary(
            s => s.ScopeId,
            s => (IReadOnlyList<IsoListingEntry>)s.Filenames
                .Select(f => new IsoListingEntry(f, $"[ds] {s.ScopeId}/{f}"))
                .ToList()));
    }

    // The single-provider case has to stay indistinguishable from the pre-fan-out behavior, which is the
    // whole backward-compatibility invariant for a vSphere-only install.
    [Fact]
    public void SingleProvider_YieldsEveryFileWithNothingMissing()
    {
        var merged = IsoService.MergeListings(
            new[] { Listing(VmType.Vsphere, (ScopeA, new[] { "a.iso", "b.iso" })) });

        var files = merged[ScopeA];
        Assert.Equal(new[] { "a.iso", "b.iso" }, files.Select(f => f.Filename).OrderBy(f => f));
        Assert.All(files, f => Assert.Empty(f.MissingProviders));
    }

    [Fact]
    public void BothProviders_OverlappingAndDisjointFiles_RecordWhereEachIsMissing()
    {
        var merged = IsoService.MergeListings(
            new[]
            {
                Listing(VmType.Vsphere, (ScopeA, new[] { "shared.iso", "vsphere-only.iso" })),
                Listing(VmType.Proxmox, (ScopeA, new[] { "shared.iso", "proxmox-only.iso" }))
            });

        var byName = merged[ScopeA].ToDictionary(f => f.Filename);

        Assert.Equal(3, byName.Count);
        Assert.Empty(byName["shared.iso"].MissingProviders);
        Assert.Equal(new[] { VmType.Proxmox }, byName["vsphere-only.iso"].MissingProviders);
        Assert.Equal(new[] { VmType.Vsphere }, byName["proxmox-only.iso"].MissingProviders);
    }

    // A provider whose listing failed is excluded by the caller, so it must not turn up in
    // MissingProviders - otherwise a transient outage would paint every row on every other hypervisor
    // as incomplete. Structural now: the available set IS the set of listings handed in, so an excluded
    // provider cannot be reported missing by construction.
    [Fact]
    public void ProviderExcludedFromTheAvailableSet_IsNotReportedMissing()
    {
        // Proxmox is enabled but its listing threw, so only vSphere's is passed in.
        var merged = IsoService.MergeListings(
            new[] { Listing(VmType.Vsphere, (ScopeA, new[] { "a.iso" })) });

        Assert.Empty(merged[ScopeA].Single().MissingProviders);
    }

    // The same upload can come back cased differently from a case-preserving store and a normalizing
    // one; showing it twice would be worse than settling on one spelling.
    [Fact]
    public void FilenamesAreMergedCaseInsensitively()
    {
        var merged = IsoService.MergeListings(
            new[]
            {
                Listing(VmType.Vsphere, (ScopeA, new[] { "Tools.iso" })),
                Listing(VmType.Proxmox, (ScopeA, new[] { "tools.iso" }))
            });

        var file = Assert.Single(merged[ScopeA]);
        Assert.Empty(file.MissingProviders);
    }

    [Fact]
    public void ScopesAreKeptSeparate()
    {
        var merged = IsoService.MergeListings(
            new[]
            {
                Listing(VmType.Vsphere, (ScopeA, new[] { "a.iso" })),
                Listing(VmType.Proxmox, (ScopeB, new[] { "b.iso" }))
            });

        Assert.Equal(new[] { VmType.Proxmox }, merged[ScopeA].Single().MissingProviders);
        Assert.Equal(new[] { VmType.Vsphere }, merged[ScopeB].Single().MissingProviders);
    }

    [Fact]
    public void NoListings_YieldsNoScopes()
    {
        var merged = IsoService.MergeListings(Array.Empty<IsoService.ProviderListing>());

        Assert.Empty(merged);
    }

    // ---- BuildViewIsoResultsAsync: a listing nobody could answer is an error, not an empty listing ----

    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static IIsoProvider FakeProvider(
        VmType type, IReadOnlyDictionary<Guid, IReadOnlyList<IsoListingEntry>> listing)
    {
        var provider = Substitute.For<IIsoProvider>();
        provider.Enabled.Returns(true);
        provider.ProviderType.Returns(type);

        if (listing == null)
        {
            provider.ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoListingEntry>>>>(
                    _ => throw new Exception($"{type} is unreachable"));
        }
        else
        {
            provider.ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(listing);
        }

        return provider;
    }

    private static Task<ManagedIsoResult[]> BuildViewIsos(params IIsoProvider[] providers)
    {
        var service = new IsoService(
            Substitute.For<IPlayerService>(),
            Substitute.For<IViewService>(),
            providers,
            new IsoUploadOptions(),
            NullLogger<IsoService>.Instance);

        return service.BuildViewIsoResultsAsync(
            [new ViewTeams(new View { Id = ViewId, Name = "view" }, [])],
            CancellationToken.None);
    }

    // "No files" is rendered next to a Delete button and read as "the upload never landed", so it must
    // not be what a total outage looks like. The Files tab already renders an API error.
    [Fact]
    public async Task EveryProviderFailingToList_Throws()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => BuildViewIsos(
            FakeProvider(VmType.Vsphere, null),
            FakeProvider(VmType.Proxmox, null)));

        Assert.Equal(
            "Could not list ISOs from Vsphere and Proxmox. Try again, or contact an administrator if the issue persists.",
            ex.Message);

        // The first provider's own failure is kept as the cause, for the log.
        Assert.Equal("Vsphere is unreachable", ex.InnerException?.Message);
    }

    [Fact]
    public async Task TheOnlyProviderFailingToList_Throws()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => BuildViewIsos(FakeProvider(VmType.Proxmox, null)));

        Assert.Contains("Could not list ISOs from Proxmox.", ex.Message);
    }

    // One provider down still degrades quietly - that is what MissingProviders is for.
    [Fact]
    public async Task OneProviderFailingToList_StillReturnsTheOthersFiles()
    {
        var results = await BuildViewIsos(
            FakeProvider(VmType.Vsphere, new Dictionary<Guid, IReadOnlyList<IsoListingEntry>>
            {
                [ViewId] = [new IsoListingEntry("a.iso", "[ds] a.iso")]
            }),
            FakeProvider(VmType.Proxmox, null));

        var iso = Assert.Single(Assert.Single(results).Isos);
        Assert.Equal("a.iso", iso.Filename);

        // Proxmox is not reported missing the file: it never answered, so nothing is known about it.
        Assert.Empty(iso.MissingProviders);
    }

    // A vSphere-only install with ISOs switched off everywhere has nothing to fail, so an empty listing
    // is the honest answer rather than an error.
    [Fact]
    public async Task NoEnabledProviders_ReturnsAnEmptyListing()
    {
        var results = await BuildViewIsos();

        Assert.Empty(Assert.Single(results).Isos);
    }
}
