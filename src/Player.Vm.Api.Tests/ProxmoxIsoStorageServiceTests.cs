// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net;
using Corsinvest.ProxmoxVE.Api;
using Player.Vm.Api.Domain.Proxmox.Services;
using Xunit;

namespace Player.Vm.Api.Tests;

// Node selection, which is the whole of this service that needs no cluster: given what PVE reports
// about the ISO storage, which node an ISO operation runs through. Everything else here is PVE calls.
//
// The rule has no failover on purpose. On a non-shared storage the node an ISO is written through
// decides which VMs can ever mount it, so a retry that landed elsewhere is exactly the bug that
// scatters a View's ISOs across a cluster.
public class ProxmoxIsoStorageServiceTests
{
    private static ProxmoxIsoStorageService.IsoStorageCandidate Candidate(string node, bool shared = true) =>
        new(node, shared);

    private static string Choose(params ProxmoxIsoStorageService.IsoStorageCandidate[] candidates) =>
        ProxmoxIsoStorageService.ChooseNode("nfs", candidates);

    // A single-node cluster, or a storage only one node offers: unambiguous, so it needs no
    // configuration at all. Shared or not does not matter - there is nowhere else to write and nowhere
    // else a Vm could be.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneCandidate_IsUsedWhetherOrNotTheStorageIsShared(bool shared)
    {
        Assert.Equal("pve1", Choose(Candidate("pve1", shared)));
    }

    // PVE reports one entry per node, so the same node can appear more than once; that is still one
    // node and must not be treated as an ambiguous cluster.
    [Fact]
    public void RepeatsOfOneNode_AreStillOneNode()
    {
        Assert.Equal("pve1", Choose(Candidate("pve1", false), Candidate("pve1", false)));
    }

    // Any node holding a shared storage is equivalent, so the choice spreads concurrent uploads rather
    // than pinning every operation to one node. The property that matters is that work reaches every
    // candidate, not that it arrives in a fixed order - so this asserts over enough calls that a random
    // choice cannot plausibly miss a node (3 * 0.667^100), while a hardcoded pick fails immediately.
    [Fact]
    public void SeveralSharedCandidates_AreAllVisited()
    {
        var candidates = new[] { Candidate("pve1"), Candidate("pve2"), Candidate("pve3") };

        var visited = Enumerable.Range(0, 100).Select(_ => Choose(candidates)).ToList();

        Assert.Equal(3, visited.Distinct(StringComparer.Ordinal).Count());
        Assert.All(visited, node => Assert.Contains(node, new[] { "pve1", "pve2", "pve3" }));
    }

    // Each node has its own copy of the storage, so an upload through pve1 is invisible to a Vm on
    // pve2. Nothing here can pick correctly on the operator's behalf, so it is a configuration error
    // rather than something to guess at - and the message has to say what will fix it.
    [Fact]
    public void SeveralNonSharedCandidates_ThrowTellingTheOperatorToUseASharedStorage()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Choose(Candidate("pve1", false), Candidate("pve2", false)));

        Assert.Contains("shared", ex.Message);
        Assert.Contains("pve1", ex.Message);
        Assert.Contains("pve2", ex.Message);
    }

    // Mixed reporting is not a shared storage: 'all shared' is the only safe reading, because one node
    // holding a private copy is enough to scatter the files.
    [Fact]
    public void SeveralCandidatesWhereOnlySomeAreShared_Throw()
    {
        Assert.Throws<InvalidOperationException>(
            () => Choose(Candidate("pve1"), Candidate("pve2", false)));
    }

    // Nothing online offers the storage - a misconfigured storage name, or a cluster that is down.
    [Fact]
    public void NoCandidates_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Choose());

        Assert.Contains("nfs", ex.Message);
    }

    // ---- IsAlreadyGone: what makes a failed delete idempotent rather than swallowed ----

    private const string VolumeId = "nfs:iso/a.iso";

    // The shape PVE's client produces for a failed call: the response is an ExpandoObject carrying an
    // 'errors' member, which is the only thing Result.GetError() reads.
    private static Result Failure(HttpStatusCode statusCode, string message)
    {
        dynamic errors = new ExpandoObject();
        errors.volid = message;

        dynamic response = new ExpandoObject();
        response.errors = errors;

        return new Result(
            response,
            statusCode,
            statusCode.ToString(),
            false,
            "/nodes/pve1/storage/nfs/content",
            new Dictionary<string, object>(),
            MethodType.Delete,
            ResponseType.Json,
            TimeSpan.Zero);
    }

    // The case this exists for: PVE reports a volume that is not there as a 500 naming it, and a delete
    // of something already deleted is the state the caller asked for.
    [Theory]
    [InlineData("unable to parse volume ID 'nfs:iso/a.iso' - does not exist")]
    [InlineData("no such file 'nfs:iso/a.iso'")]
    public void AMissingVolume_IsAlreadyGone(string message)
    {
        Assert.True(ProxmoxIsoStorageService.IsAlreadyGone(
            Failure(HttpStatusCode.InternalServerError, message), VolumeId));
    }

    // Naming a different volume means something other than our delete target is missing, which is a
    // real failure - our file may well still be there.
    [Fact]
    public void AMissingPhraseAboutAnotherVolume_IsNotAlreadyGone()
    {
        Assert.False(ProxmoxIsoStorageService.IsAlreadyGone(
            Failure(HttpStatusCode.InternalServerError, "storage 'other' does not exist"), VolumeId));
    }

    // A 500 that is not about a missing file must surface, or a storage outage would report every
    // delete as a success.
    [Fact]
    public void AnUnrelated500_IsNotAlreadyGone()
    {
        Assert.False(ProxmoxIsoStorageService.IsAlreadyGone(
            Failure(HttpStatusCode.InternalServerError, "storage 'nfs:iso/a.iso' is not online"), VolumeId));
    }

    // An authorization failure names nothing about the file's existence, so it can never be read as a
    // completed delete - the file is still there and the token simply cannot remove it.
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public void ANonServerErrorStatus_IsNotAlreadyGone(HttpStatusCode statusCode)
    {
        Assert.False(ProxmoxIsoStorageService.IsAlreadyGone(
            Failure(statusCode, "permission check failed for 'nfs:iso/a.iso' - does not exist"), VolumeId));
    }
}
