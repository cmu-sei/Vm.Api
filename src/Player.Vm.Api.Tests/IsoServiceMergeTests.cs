// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files;
using Xunit;

namespace Player.Vm.Api.Tests;

// Covers IsoService.MergeListings, which is what the Files tab shows: one row per filename per scope,
// with MissingProviders recording the hypervisors that do not have the file.
public class IsoServiceMergeTests
{
    private static readonly Guid ScopeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ScopeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>> Listing(
        VmType provider,
        params (Guid ScopeId, string[] Filenames)[] scopes)
    {
        return scopes.ToDictionary(
            s => s.ScopeId,
            s => (IReadOnlyList<IsoFile>)s.Filenames
                .Select(f => new IsoFile($"[ds] {s.ScopeId}/", f)
                {
                    MountValue = $"[ds] {s.ScopeId}/{f}",
                    ProviderType = provider
                })
                .ToList());
    }

    // The single-provider case has to stay indistinguishable from the pre-fan-out behavior, which is the
    // whole backward-compatibility invariant for a vSphere-only install.
    [Fact]
    public void SingleProvider_YieldsEveryFileWithNothingMissing()
    {
        var merged = IsoService.MergeListings(
            new[] { VmType.Vsphere },
            new[] { Listing(VmType.Vsphere, (ScopeA, new[] { "a.iso", "b.iso" })) });

        var files = merged[ScopeA];
        Assert.Equal(new[] { "a.iso", "b.iso" }, files.Select(f => f.Filename).OrderBy(f => f));
        Assert.All(files, f => Assert.Empty(f.MissingProviders));
    }

    [Fact]
    public void BothProviders_OverlappingAndDisjointFiles_RecordWhereEachIsMissing()
    {
        var merged = IsoService.MergeListings(
            new[] { VmType.Vsphere, VmType.Proxmox },
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

    // A merged row must never carry a mount token: the file may sit on more than one hypervisor, and the
    // Files tab does not mount. Mount pickers get their rows from BuildVmIsoResultsAsync instead.
    [Fact]
    public void MergedRows_CarryNoMountInformation()
    {
        var merged = IsoService.MergeListings(
            new[] { VmType.Vsphere },
            new[] { Listing(VmType.Vsphere, (ScopeA, new[] { "a.iso" })) });

        var file = merged[ScopeA].Single();
        Assert.Null(file.Path);
        Assert.Null(file.MountValue);
        Assert.Null(file.ProviderType);
        Assert.Null(file.ProviderInstanceId);
    }

    // A provider whose listing failed is excluded by the caller, so it must not turn up in
    // MissingProviders - otherwise a transient outage would paint every row on every other hypervisor
    // as incomplete.
    [Fact]
    public void ProviderExcludedFromTheAvailableSet_IsNotReportedMissing()
    {
        var merged = IsoService.MergeListings(
            new[] { VmType.Vsphere },   // Proxmox listing threw, so it is not available
            new[] { Listing(VmType.Vsphere, (ScopeA, new[] { "a.iso" })) });

        Assert.Empty(merged[ScopeA].Single().MissingProviders);
    }

    // The same upload can come back cased differently from a case-preserving store and a normalizing
    // one; showing it twice would be worse than settling on one spelling.
    [Fact]
    public void FilenamesAreMergedCaseInsensitively()
    {
        var merged = IsoService.MergeListings(
            new[] { VmType.Vsphere, VmType.Proxmox },
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
            new[] { VmType.Vsphere, VmType.Proxmox },
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
        var merged = IsoService.MergeListings(
            Array.Empty<VmType>(),
            Array.Empty<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>>());

        Assert.Empty(merged);
    }
}
