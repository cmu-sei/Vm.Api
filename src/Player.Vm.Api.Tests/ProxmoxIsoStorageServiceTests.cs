// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
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

    // No configured node, so the policy has to resolve the cluster on its own.
    private static string Choose(params ProxmoxIsoStorageService.IsoStorageCandidate[] candidates) =>
        ProxmoxIsoStorageService.ChooseNode(null, "nfs", candidates);

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
    // rather than something to guess at - and the message has to say what to set.
    [Fact]
    public void SeveralNonSharedCandidates_ThrowTellingTheOperatorToPinANode()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Choose(Candidate("pve1", false), Candidate("pve2", false)));

        Assert.Contains("IsoNode", ex.Message);
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
            () => ProxmoxIsoStorageService.ChooseNode(
                null, "nfs", Array.Empty<ProxmoxIsoStorageService.IsoStorageCandidate>()));

        Assert.Contains("nfs", ex.Message);
    }

    // Proxmox:IsoNode wins outright, and it is the escape hatch for exactly the cluster the policy
    // refuses to guess at: several nodes, storage not shared. The caller passes no candidates at all
    // when a node is pinned - it never queries PVE - so a pin must not depend on discovery having run.
    [Fact]
    public void APinnedNode_WinsOutrightWithoutConsultingTheCluster()
    {
        Assert.Equal("pve2", ProxmoxIsoStorageService.ChooseNode("pve2", "nfs", null));

        // Even over candidates that would otherwise be rejected as ambiguous.
        Assert.Equal("pve2", ProxmoxIsoStorageService.ChooseNode(
            "pve2", "nfs", [Candidate("pve1", false), Candidate("pve3", false)]));
    }

    // A blank setting is not a pin - IsoNode is unset in every deployment that has not needed it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankPinnedNode_FallsBackToTheCluster(string configuredNode)
    {
        Assert.Equal("pve1", ProxmoxIsoStorageService.ChooseNode(configuredNode, "nfs", [Candidate("pve1")]));
    }
}
