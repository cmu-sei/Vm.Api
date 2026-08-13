// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Extension;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Proxmox.Extensions;
using Player.Vm.Api.Domain.Proxmox.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Domain.Proxmox.Services;

/// <summary>
/// The PVE storage mechanics behind Proxmox ISO support: which node to talk to, and the list, upload
/// and delete calls against the configured ISO storage.
/// </summary>
/// <remarks>
/// Separate from <see cref="IProxmoxService"/>, which owns Vm lifecycle, snapshots, guest operations
/// and networks. Mounting an ISO stays there, because it reconfigures a Vm rather than touching a
/// storage. What belongs to a storage - node discovery, the long-timeout upload client, task polling
/// for a storage task - lives here.
/// </remarks>
public interface IProxmoxIsoStorageService
{
    /// <summary>
    /// Every ISO on the configured ISO storage. Used for the management listing, where any node's
    /// view of the storage will do.
    /// </summary>
    Task<IReadOnlyList<ProxmoxIsoVolume>> ListIsos(CancellationToken cancellationToken);

    /// <summary>
    /// The same listing, but read from the node the given Vm currently runs on, so every volume id
    /// returned is one that Vm can actually mount. Matters on a storage that is not shared.
    /// </summary>
    Task<IReadOnlyList<ProxmoxIsoVolume>> ListIsosForVm(Guid vmId, CancellationToken cancellationToken);

    /// <summary>
    /// Pushes a local file to the ISO storage through PVE's own upload API (UploadViaApi mode).
    /// Overwrites an existing file of the same name.
    /// </summary>
    Task UploadIso(string encodedFileName, string localFilePath, CancellationToken cancellationToken);

    /// <summary>
    /// Removes an ISO from the ISO storage through PVE's API. A file that is already gone is success.
    /// </summary>
    Task DeleteIso(string encodedFileName, CancellationToken cancellationToken);
}

public class ProxmoxIsoStorageService : IProxmoxIsoStorageService
{
    // PVE's content-type discriminator for ISO images, used for both listing and upload.
    private const string IsoContentType = "iso";

    private readonly ProxmoxOptions _options;
    private readonly ILogger<ProxmoxIsoStorageService> _logger;
    private readonly PveClient _pveClient;
    private readonly IProxmoxService _proxmoxService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _uploadSecondsTimeout;

    public ProxmoxIsoStorageService(
        ProxmoxOptions options,
        ILogger<ProxmoxIsoStorageService> logger,
        IProxmoxService proxmoxService,
        IHttpClientFactory httpClientFactory,
        IsoUploadOptions isoUploadOptions)
    {
        _options = options;
        _logger = logger;
        _proxmoxService = proxmoxService;
        _httpClientFactory = httpClientFactory;

        // The same cross-provider budget the vSphere datastore upload uses, in the seconds the SDK wants.
        _uploadSecondsTimeout = (isoUploadOptions.UploadTimeoutMinutes <= 0 ? 60 : isoUploadOptions.UploadTimeoutMinutes) * 60;

        _pveClient = new PveClient(options.Host, options.Port, httpClientFactory.CreateClient("proxmox"))
        {
            ApiToken = options.Token
        };
    }

    public async Task<IReadOnlyList<ProxmoxIsoVolume>> ListIsos(CancellationToken cancellationToken)
    {
        var node = await ResolveIsoStorageNode();
        return await ListIsosOnNode(node, cancellationToken);
    }

    public async Task<IReadOnlyList<ProxmoxIsoVolume>> ListIsosForVm(Guid vmId, CancellationToken cancellationToken)
    {
        EnsureIsoStorageConfigured();

        // Deliberately the Vm's own node rather than a discovered one: the point of this listing is
        // that every volume id it returns is mountable by THIS Vm, which on a non-shared storage is
        // only true of the node the Vm is on.
        var node = await _proxmoxService.GetCurrentNodeForVm(vmId, cancellationToken);

        if (node == null)
            return [];

        return await ListIsosOnNode(node, cancellationToken);
    }

    public async Task UploadIso(string encodedFileName, string localFilePath, CancellationToken cancellationToken)
    {
        var node = await ResolveIsoStorageNode();

        using var fileStream = File.OpenRead(localFilePath);

        // A client that has not yet sent a request: UploadFileToStorageAsync assigns
        // HttpClient.Timeout on every invocation, and that setter throws once an instance has sent
        // one. See CreateUploadClient.
        var result = await CreateUploadClient().UploadFileToStorageAsync(
            node,
            _options.IsoStorage,
            IsoContentType,
            fileStream,
            encodedFileName,
            cancellationToken,
            secondsTimeout: _uploadSecondsTimeout);

        await _pveClient.WaitAndThrow(result, $"UploadIso node={node} file={encodedFileName}", cancellationToken);
    }

    public async Task DeleteIso(string encodedFileName, CancellationToken cancellationToken)
    {
        var node = await ResolveIsoStorageNode();
        var volumeId = ProxmoxIsoNaming.BuildVolumeId(_options.IsoStorage, encodedFileName);

        var result = await _pveClient.Nodes[node].Storage[_options.IsoStorage].Content[volumeId].Delete();

        if (IsAlreadyGone(result, volumeId))
        {
            _logger.LogInformation(
                "ISO {File} was already absent from Proxmox storage {Storage} on node {Node}",
                encodedFileName, _options.IsoStorage, node);
            return;
        }

        await _pveClient.WaitAndThrow(result, $"DeleteIso node={node} file={encodedFileName}", cancellationToken);
    }

    /// <summary>
    /// Whether a failed delete failed only because the volume was not there, which makes the delete
    /// idempotent - already gone is the state the caller asked for.
    /// </summary>
    /// <remarks>
    /// PVE reports a missing volume as a 500 carrying "does not exist" rather than a 404, and only in
    /// the flattened error text, so there is no structured signal to key on. All three conditions are
    /// required so that an unrelated 500, or a 401/403 that happens to mention a missing something,
    /// cannot be read as a successful delete: the status has to be exactly the 500 PVE uses, and the
    /// error has to name this volume as well as matching a known phrase.
    /// </remarks>
    internal static bool IsAlreadyGone(Result result, string volumeId)
    {
        if (result.IsSuccessStatusCode || result.StatusCode != HttpStatusCode.InternalServerError)
            return false;

        var error = result.GetError() ?? string.Empty;

        if (!error.Contains(volumeId, StringComparison.OrdinalIgnoreCase))
            return false;

        return error.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no such file", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<ProxmoxIsoVolume>> ListIsosOnNode(string node, CancellationToken cancellationToken)
    {
        var contents = await _pveClient.Nodes[node].Storage[_options.IsoStorage].Content
            .GetAsync(IsoContentType);

        return (contents ?? [])
            .Where(x => !string.IsNullOrEmpty(x.Volume))
            .Select(x => new ProxmoxIsoVolume(
                x.Volume,
                ProxmoxIsoNaming.VolumeFileName(x.Volume),
                x.Size))
            .ToList();
    }

    /// <summary>
    /// The node to run a storage operation on.
    /// </summary>
    private async Task<string> ResolveIsoStorageNode()
    {
        EnsureIsoStorageConfigured();

        return ChooseNode(_options.IsoStorage, await FindIsoStorageCandidates());
    }

    /// <summary>Every online node PVE currently reports the configured ISO storage on.</summary>
    private async Task<IReadOnlyList<IsoStorageCandidate>> FindIsoStorageCandidates()
    {
        // One cluster-wide call rather than a storage query per node. Note GetResourcesAsync returns a
        // single ClusterResource type that implements every resource interface, so the resource kind
        // has to be filtered on ResourceType - an OfType<IClusterResourceStorage>() would match
        // everything.
        var resources = await _pveClient.GetResourcesAsync(ClusterResourceType.Storage);

        var candidates = resources
            .Where(x => x.ResourceType == ClusterResourceType.Storage
                && string.Equals(x.Storage, _options.IsoStorage, StringComparison.Ordinal)
                && x.IsAvailable
                && !string.IsNullOrWhiteSpace(x.Node))
            .Select(x => new IsoStorageCandidate(x.Node, x.Shared))
            .ToList();

        return candidates;
    }

    /// <summary>One node PVE reports the ISO storage as being available on.</summary>
    internal readonly record struct IsoStorageCandidate(string Node, bool Shared);

    /// <summary>
    /// Picks the one node an ISO operation runs through.
    /// </summary>
    /// <remarks>
    /// One node, with no failover: a retry that lands on a different node is only safe on a shared
    /// storage, and on a non-shared one it is what scatters a View's ISOs across the cluster - an
    /// upload on node A that a Vm on node B cannot see. So a cluster that cannot be resolved to an
    /// unambiguous node is a configuration error rather than something to guess at.
    ///
    /// On a shared storage with several nodes any of them is equivalent, so the node is picked at
    /// random to spread concurrent uploads rather than pinning every operation to one node.
    ///
    /// Internal and separate from the PVE query so the policy can be tested without a cluster.
    /// </remarks>
    internal static string ChooseNode(string storageName, IReadOnlyList<IsoStorageCandidate> candidates)
    {
        var nodes = candidates
            .Select(x => x.Node)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (nodes.Count == 0)
        {
            throw new InvalidOperationException(
                $"No online Proxmox node currently offers ISO storage '{storageName}'.");
        }

        // Unambiguous, so nothing to decide and nothing to configure - a single-node cluster, or a
        // storage only one node offers. Shared or not does not matter: there is nowhere else to write
        // and nowhere else a Vm could be.
        if (nodes.Count == 1)
            return nodes[0];

        if (!candidates.All(x => x.Shared))
        {
            // Every node has its own copy of this storage, so which node an ISO was written through
            // decides which VMs can ever mount it. Nothing here can pick correctly on the operator's
            // behalf.
            throw new InvalidOperationException(
                $"Proxmox ISO storage '{storageName}' is not shared and is offered by {nodes.Count} nodes ({string.Join(", ", nodes)}), so an ISO written through one node would not be mountable by VMs on the others. Point Proxmox:IsoStorage at a shared storage instead.");
        }

        return nodes[Random.Shared.Next(nodes.Count)];
    }

    private void EnsureIsoStorageConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.IsoStorage))
            throw new InvalidOperationException("Proxmox ISO support requires Proxmox:IsoStorage to be set.");
    }

    // A separate client on the long-timeout "proxmoxIsoUpload" HttpClient, because
    // UploadFileToStorageAsync sends the body through whichever HttpClient its PveClient was built
    // with, and the shared one keeps HttpClient's 100 second default - far too short for a
    // multi-gigabyte ISO, while raising it there would slow failure detection in the state and task
    // pollers.
    //
    // Deliberately built fresh per upload rather than cached: UploadFileToStorageAsync assigns
    // HttpClient.Timeout on every invocation, and that setter throws once the instance has sent a
    // request. A reused client therefore fails every upload after the first - which is one per team on
    // a multi-team upload. IHttpClientFactory pools the handler, so a fresh HttpClient costs nothing.
    private PveClient CreateUploadClient() =>
        new(_options.Host, _options.Port, _httpClientFactory.CreateClient("proxmoxIsoUpload"))
        {
            ApiToken = _options.Token
        };
}
