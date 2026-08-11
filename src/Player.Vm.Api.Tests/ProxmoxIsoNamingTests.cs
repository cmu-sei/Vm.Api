// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Player.Vm.Api.Domain.Proxmox;
using Xunit;

namespace Player.Vm.Api.Tests;

public class ProxmoxIsoNamingTests
{
    private const string Sep = "__";

    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ScopeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Encode_Then_TryDecode_RoundTrips()
    {
        var encoded = ProxmoxIsoNaming.Encode(ViewId, ScopeId.ToString(), "tools.iso", Sep);

        Assert.Equal($"{ViewId}{Sep}{ScopeId}{Sep}tools.iso", encoded);
        Assert.True(ProxmoxIsoNaming.TryDecode(encoded, Sep, out var viewId, out var scopeId, out var displayName));
        Assert.Equal(ViewId, viewId);
        Assert.Equal(ScopeId, scopeId);
        Assert.Equal("tools.iso", displayName);
    }

    // Everything the storage might hold that is not ours has to be skipped rather than surfaced under
    // some arbitrary View - PVE's own templates, hand-placed ISOs, and TopoMojo's 2-segment names.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("debian-12.iso")]                                                   // no scoping at all
    [InlineData("11111111-1111-1111-1111-111111111111__tools.iso")]                 // TopoMojo-style, 2 segments
    [InlineData("11111111-1111-1111-1111-111111111111__22222222-2222-2222-2222-222222222222__a__b.iso")]  // 4 segments
    [InlineData("not-a-guid__22222222-2222-2222-2222-222222222222__tools.iso")]      // viewId not a guid
    [InlineData("11111111-1111-1111-1111-111111111111__not-a-guid__tools.iso")]      // scopeId not a guid
    [InlineData("11111111-1111-1111-1111-111111111111__22222222-2222-2222-2222-222222222222__tools.img")] // not an iso
    [InlineData("11111111-1111-1111-1111-111111111111__22222222-2222-2222-2222-222222222222__")]          // empty name
    public void TryDecode_Rejects_AnythingNotExactlyOurShape(string fileName)
    {
        Assert.False(ProxmoxIsoNaming.TryDecode(fileName, Sep, out var viewId, out var scopeId, out var displayName));
        Assert.Equal(Guid.Empty, viewId);
        Assert.Equal(Guid.Empty, scopeId);
        Assert.Null(displayName);
    }

    [Fact]
    public void TryDecode_Rejects_EmptySeparator()
    {
        var encoded = ProxmoxIsoNaming.Encode(ViewId, ScopeId.ToString(), "tools.iso", Sep);

        Assert.False(ProxmoxIsoNaming.TryDecode(encoded, "", out _, out _, out _));
    }

    // PVE reports the volume id one way for a dir/nfs storage and another for others; the filename
    // itself never contains a '/', so the last segment is the filename in both.
    [Theory]
    [InlineData("nfs:iso/tools.iso", "tools.iso")]
    [InlineData("nfs:/iso/tools.iso", "tools.iso")]
    [InlineData("tools.iso", "tools.iso")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void VolumeFileName_TakesTheLastSegment(string volumeId, string expected)
    {
        Assert.Equal(expected, ProxmoxIsoNaming.VolumeFileName(volumeId));
    }

    [Fact]
    public void BuildVolumeId_UsesPvesIsoLayout()
    {
        Assert.Equal("nfs:iso/tools.iso", ProxmoxIsoNaming.BuildVolumeId("nfs", "tools.iso"));
    }

    // The whole point of normalizing ourselves: PVE's upload API rewrites [^-a-zA-Z0-9_.] to '_', so a
    // name we did not normalize would come back different from the one delete and mount reconstruct.
    [Theory]
    [InlineData("Win 10 (x64).iso", "Win_10_x64_.iso")]
    [InlineData("already-clean_1.0.iso", "already-clean_1.0.iso")]
    [InlineData("a+b~c.iso", "a_b_c.iso")]
    [InlineData("many____underscores.iso", "many_underscores.iso")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Normalize_FoldsToPvesCharsetAndCollapsesRuns(string filename, string expected)
    {
        Assert.Equal(expected, ProxmoxIsoNaming.Normalize(filename));
    }

    // A normalized name must never contain the '__' separator, or TryDecode would read too many
    // segments. Collapsing runs is what guarantees it.
    [Fact]
    public void Normalize_NeverProducesTheSeparator()
    {
        Assert.DoesNotContain(Sep, ProxmoxIsoNaming.Normalize("Win 10 (x64) [en-US].iso"));
    }

    // The charset rule on its own, which is what the separator is checked against. The default '__' has
    // to pass: it is not run-collapse safe, and it does not need to be.
    [Theory]
    [InlineData("__", true)]
    [InlineData("_", true)]
    [InlineData("-.", true)]
    [InlineData("#", false)]
    [InlineData("##", false)]
    [InlineData(" ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SurvivesUpload_ChecksOnlyTheCharset(string value, bool expected)
    {
        Assert.Equal(expected, ProxmoxIsoNaming.SurvivesUpload(value));
    }

    // IsoService applies one normalizer per enabled provider, so a second pass has to be a no-op.
    [Theory]
    [InlineData("Win 10 (x64).iso")]
    [InlineData("a b  c.iso")]
    [InlineData("plain.iso")]
    public void Normalize_IsIdempotent(string filename)
    {
        var once = ProxmoxIsoNaming.Normalize(filename);

        Assert.Equal(once, ProxmoxIsoNaming.Normalize(once));
    }

    // An already-normalized display name still round-trips, which is the sequence a real upload takes:
    // normalize, validate, encode, then decode on the way back out of the listing.
    [Fact]
    public void Normalize_Then_Encode_Then_TryDecode_RoundTrips()
    {
        var normalized = ProxmoxIsoNaming.Normalize("Win 10 (x64).iso");
        var encoded = ProxmoxIsoNaming.Encode(ViewId, ScopeId.ToString(), normalized, Sep);

        Assert.True(ProxmoxIsoNaming.TryDecode(encoded, Sep, out _, out _, out var displayName));
        Assert.Equal(normalized, displayName);
    }
}
