// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Extension;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Node;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Vm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Extensions;
using Player.Vm.Api.Domain.Proxmox.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Domain.Proxmox.Services;

public interface IProxmoxService
{
    Task<ProxmoxConsole> GetConsole(ProxmoxVmInfo info);
    Task<Dictionary<string, string>> GetCurrentNetworks(
        ProxmoxVmInfo info,
        CancellationToken cancellationToken);
    NicOptions GetNicOptions(
        Dictionary<string, string> currentNetworks,
        IDictionary<string, string> allowedNetworks,
        IDictionary<string, string> networkNames);
    Task ChangeNetwork(
        ProxmoxVmInfo info,
        string adapter,
        string network,
        CancellationToken cancellationToken);
    Task<IEnumerable<IClusterResourceVm>> GetVms();
    Task<IEnumerable<NodeTask>> GetTasks();

    Task<string> PowerOnVm(ProxmoxVmInfo info);
    Task<string> PowerOffVm(ProxmoxVmInfo info);
    Task<string> RebootVm(ProxmoxVmInfo info);
    Task<string> ShutdownVm(ProxmoxVmInfo info);

    /// <summary>
    /// Submits a power operation for many Vms at once without waiting for the Proxmox tasks to
    /// finish. Returns a per-Vm error message, or an empty string for a Vm whose operation was
    /// accepted. Completion is observed by <see cref="IProxmoxTaskService"/>.
    /// </summary>
    Task<Dictionary<Guid, string>> BulkPowerOperation(Guid[] ids, PowerOperation operation);

    Task<GuestProcessResult> RunGuestProcess(ProxmoxVmInfo info, string command, string arguments, TimeSpan timeout);
    Task<long> RunGuestProcessFast(ProxmoxVmInfo info, string command, string arguments);
    Task<string> ReadGuestFile(ProxmoxVmInfo info, string guestFilePath);
    Task<string> UploadFileToGuest(ProxmoxVmInfo info, string guestFilePath, Stream content);

    /// <summary>
    /// Every ISO on the configured ISO storage, read from whichever node currently offers it. Used for
    /// the management listing, where any node's view of a shared storage will do.
    /// </summary>
    Task<IReadOnlyList<ProxmoxIsoVolume>> ListStorageIsos(CancellationToken cancellationToken);

    /// <summary>
    /// The same listing, but read from the node the given Vm currently runs on, so every volume id
    /// returned is one that Vm can actually mount. Matters on a storage that is not shared.
    /// </summary>
    Task<IReadOnlyList<ProxmoxIsoVolume>> ListStorageIsosForVm(Guid vmId, CancellationToken cancellationToken);

    /// <summary>
    /// Pushes a local file to the ISO storage through PVE's own upload API (UploadToStorage mode).
    /// Overwrites an existing file of the same name.
    /// </summary>
    Task UploadIsoToStorage(string encodedFileName, string localFilePath, CancellationToken cancellationToken);

    /// <summary>
    /// Removes an ISO from the ISO storage through PVE's API. A file that is already gone is success.
    /// </summary>
    Task DeleteIsoFromStorage(string encodedFileName, CancellationToken cancellationToken);

    /// <summary>
    /// Swaps the medium in a running or stopped QEMU Vm's existing CD-ROM drive.
    /// </summary>
    Task MountIso(ProxmoxVmInfo info, string isoVolumeId, CancellationToken cancellationToken);

    Task<List<ProxmoxSnapshot>> GetSnapshots(ProxmoxVmInfo info);
    Task<string> CreateSnapshot(ProxmoxVmInfo info, string snapshotName, string description, bool includeRam);
    Task<string> RevertSnapshot(ProxmoxVmInfo info, string snapshotName);
    Task<string> DeleteSnapshot(ProxmoxVmInfo info, string snapshotName);
}

public class ProxmoxService : IProxmoxService
{
    // PVE's content-type discriminator for ISO images, used for both listing and upload.
    private const string IsoContentType = "iso";

    private static readonly System.Text.RegularExpressions.Regex DriveIdRegex =
        new(@"^([a-zA-Z]+)(\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly ProxmoxOptions _options;
    private readonly ILogger<ProxmoxService> _logger;
    private readonly PveClient _pveClient;
    private readonly IProxmoxStateService _proxmoxStateService;
    private readonly RewriteHostOptions _rewriteHostOptions;
    private readonly VmContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _uploadSecondsTimeout;

    public ProxmoxService(
            ProxmoxOptions options,
            ILogger<ProxmoxService> logger,
            IProxmoxStateService proxmoxStateService,
            RewriteHostOptions rewriteHostOptions,
            VmContext dbContext,
            IHttpClientFactory httpClientFactory,
            Infrastructure.Options.IsoUploadOptions isoUploadOptions
        )
    {
        _options = options;
        _logger = logger;
        _proxmoxStateService = proxmoxStateService;
        _rewriteHostOptions = rewriteHostOptions;
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;

        // The same cross-provider budget the vSphere datastore upload uses, in the seconds the SDK wants.
        _uploadSecondsTimeout = (isoUploadOptions.UploadTimeoutMinutes <= 0 ? 60 : isoUploadOptions.UploadTimeoutMinutes) * 60;

        _pveClient = new PveClient(options.Host, _options.Port, httpClientFactory.CreateClient("proxmox"));
        _pveClient.ApiToken = options.Token;

    }

    // A separate client on the long-timeout "proxmoxIsoUpload" HttpClient, because
    // UploadFileToStorageAsync sends the body through whichever HttpClient its PveClient was built
    // with, and the shared one keeps HttpClient's 100 second default - far too short for a
    // multi-gigabyte ISO, while raising it there would slow failure detection in the state and task
    // pollers.
    //
    // Deliberately built fresh per upload attempt rather than cached: UploadFileToStorageAsync
    // assigns HttpClient.Timeout on every invocation, and that setter throws once the instance has
    // sent a request. A reused client therefore fails every upload after the first - which is one
    // per team on a multi-team upload, and one per node on failover. IHttpClientFactory pools the
    // handler, so a fresh HttpClient per attempt costs nothing.
    private PveClient CreateUploadClient() =>
        new(_options.Host, _options.Port, _httpClientFactory.CreateClient("proxmoxIsoUpload"))
        {
            ApiToken = _options.Token
        };

    public async Task<ProxmoxConsole> GetConsole(ProxmoxVmInfo info)
    {
        // The power state is read up front rather than inferred from a vncproxy failure. Proxmox
        // hands out a vncproxy ticket for a Vm that is not running, but the resulting websocket
        // never completes an RFB handshake, so a client given that ticket waits forever. This also
        // refreshes info.Node, which goes stale as soon as a Vm migrates.
        var vm = await ResolveNode(info);

        if (vm == null)
        {
            // Proxmox does not know this vmid at all, which is a real error rather than a power
            // state. Reporting PowerState.Unknown here would be indistinguishable from a transient
            // failure and would leave the client retrying a console that can never exist.
            throw new Exception($"Could not find vmid {info.Id} in Proxmox");
        }

        if (!vm.IsRunning)
        {
            // A Vm that is not running has no console to proxy. Returning the state instead of
            // throwing lets the client render a powered off placeholder rather than treating a
            // normal power state as a server error.
            return new ProxmoxConsole
            {
                PowerState = vm.GetPowerState()
            };
        }

        var result = await VncProxyCall(info.Node, info.Id, info.Type);

        if (!result.IsSuccessStatusCode)
        {
            throw new Exception(result.GetError());
        }

        string url = null;
        string urlFragment = $"/api2/json/nodes/{info.Node}/{info.Type.ToString().ToLower()}/{info.Id}/vncwebsocket?port={result.Response.data.port}&vncticket={WebUtility.UrlEncode(result.Response.data.ticket)}";

        if (_rewriteHostOptions.RewriteHost)
        {
            url = $"wss://{_rewriteHostOptions.RewriteHostUrl}{urlFragment}&{_rewriteHostOptions.RewriteHostQueryParam}={_options.Host}";
        }
        else
        {
            url = $"wss://{_options.Host}{urlFragment}";
        }

        return new ProxmoxConsole()
        {
            Ticket = result.Response.data.ticket,
            Url = url,
            PowerState = PowerState.On
        };
    }

    public NicOptions GetNicOptions(
        Dictionary<string, string> currentNetworks,
        IDictionary<string, string> allowedNetworks,
        IDictionary<string, string> networkNames)
    {
        var available = (allowedNetworks ?? new Dictionary<string, string>())
            .OrderBy(x => string.IsNullOrWhiteSpace(x.Value) ? x.Key : x.Value)
            .ToDictionary(
                x => x.Key,
                x => string.IsNullOrWhiteSpace(x.Value) ? x.Key : x.Value);
        var readOnly = new List<string>();

        foreach (var currentNetwork in (currentNetworks ?? new Dictionary<string, string>()).Values)
        {
            if (string.IsNullOrWhiteSpace(currentNetwork))
                continue;

            if (!available.ContainsKey(currentNetwork))
            {
                available[currentNetwork] =
                    networkNames != null &&
                    networkNames.TryGetValue(currentNetwork, out var name) &&
                    !string.IsNullOrWhiteSpace(name)
                        ? name
                        : currentNetwork;
                readOnly.Add(currentNetwork);
            }
        }

        return new NicOptions
        {
            AvailableNetworks = available,
            CurrentNetworks = currentNetworks ?? new Dictionary<string, string>(),
            ReadOnlyNetworks = readOnly.ToArray()
        };
    }

    public async Task<Dictionary<string, string>> GetCurrentNetworks(
        ProxmoxVmInfo info,
        CancellationToken cancellationToken)
    {
        var vm = await ResolveNode(info);
        if (vm == null)
            throw new InvalidOperationException($"Could not find vmid {info.Id} in Proxmox");

        var networks = await GetNetworkConfiguration(info);
        return networks
            .Where(network =>
                !string.IsNullOrWhiteSpace(network.Id) &&
                !string.IsNullOrWhiteSpace(network.Bridge))
            .ToDictionary(network => network.Id, network => network.Bridge);
    }

    public async Task ChangeNetwork(
        ProxmoxVmInfo info,
        string adapter,
        string network,
        CancellationToken cancellationToken)
    {
        var vm = await ResolveNode(info);
        if (vm == null)
            throw new InvalidOperationException($"Could not find vmid {info.Id} in Proxmox");

        var networks = await GetNetworkConfiguration(info);
        var nic = networks.SingleOrDefault(network =>
            string.Equals(network.Id, adapter, StringComparison.Ordinal));
        if (nic == null ||
            string.IsNullOrWhiteSpace(nic.Bridge) ||
            string.IsNullOrWhiteSpace(nic.RawDefinition))
            throw new InvalidOperationException($"Could not find network adapter {adapter} on vmid {info.Id}");

        var adapterIndex = int.Parse(
            nic.Id["net".Length..],
            CultureInfo.InvariantCulture);

        await ValidateTargetNetwork(info.Node, network);

        var updatedValue = ReplaceBridge(nic.RawDefinition, network);
        var assignments = new Dictionary<int, string> { [adapterIndex] = updatedValue };

        Result result;
        if (info.Type == ProxmoxVmType.QEMU)
        {
            result = await _pveClient.Nodes[info.Node].Qemu[info.Id].Config.UpdateVmAsync(netN: assignments);
        }
        else
        {
            result = await _pveClient.Nodes[info.Node].Lxc[info.Id].Config.UpdateVm(netN: assignments);
        }

        await WaitAndThrow(
            result,
            $"ChangeNetwork vmid={info.Id} adapter={adapter}",
            cancellationToken);
        _proxmoxStateService.CheckState();
    }

    private async Task<IEnumerable<VmNetwork>> GetNetworkConfiguration(ProxmoxVmInfo info)
    {
        if (info.Type == ProxmoxVmType.QEMU)
        {
            var config = await _pveClient.Nodes[info.Node].Qemu[info.Id].Config.GetAsync(true);
            return config.Networks ?? [];
        }

        var containerConfig = await _pveClient.Nodes[info.Node].Lxc[info.Id].Config.GetAsync(true);
        return containerConfig.Networks ?? [];
    }

    private async Task ValidateTargetNetwork(string node, string network)
    {
        var result = await _pveClient.Nodes[node].Network.Index("any_bridge");
        if (!result.IsSuccessStatusCode)
            throw new Exception($"Could not list Proxmox networks on node {node}: {result.GetError()}");

        var networks = result.ToModel<NodeNetwork[]>();
        var exists = networks?.Any(item =>
            string.Equals(item.Interface, network, StringComparison.Ordinal)) == true;

        if (!exists)
        {
            throw new BadRequestException(
                $"The target network '{network}' does not exist on Proxmox node '{node}'.");
        }
    }

    private static string ReplaceBridge(string rawValue, string network)
    {
        var parts = rawValue.Split(',', StringSplitOptions.TrimEntries).ToList();
        var bridgeIndex = parts.FindIndex(x => x.StartsWith("bridge=", StringComparison.OrdinalIgnoreCase));

        if (bridgeIndex >= 0)
            parts[bridgeIndex] = $"bridge={network}";
        else
            parts.Add($"bridge={network}");

        return string.Join(',', parts);
    }

    private async Task<Result> VncProxyCall(string node, int id, ProxmoxVmType type)
    {
        if (type == ProxmoxVmType.LXC)
        {
            return await _pveClient.Nodes[node].Lxc[id].Vncproxy.Vncproxy(websocket: true);
        }
        else
        {
            return await _pveClient.Nodes[node].Qemu[id].Vncproxy.Vncproxy(websocket: true);
        }
    }

    public async Task<IEnumerable<IClusterResourceVm>> GetVms()
    {
        return await _pveClient.GetResourcesAsync(ClusterResourceType.Vm);
    }

    /// <summary>
    /// Lists recent tasks cluster-wide, regardless of which client submitted them.
    /// </summary>
    public async Task<IEnumerable<NodeTask>> GetTasks()
    {
        return await _pveClient.Cluster.Tasks.GetAsync();
    }

    /// <summary>
    /// Re-resolves the node a Vm currently lives on. ProxmoxVmInfo.Node is only refreshed by the
    /// state poller, so it goes stale as soon as a Vm migrates and every Nodes[Node] call against
    /// it fails. Returns the live cluster resource, or null if Proxmox no longer knows the vmid.
    /// </summary>
    private async Task<IClusterResourceVm> ResolveNode(ProxmoxVmInfo info)
    {
        IClusterResourceVm vm;

        try
        {
            vm = await _pveClient.GetVmAsync(info.Id);
        }
        catch (ArgumentException)
        {
            // GetVmAsync throws rather than returning null when the cluster has no such vmid.
            return null;
        }

        if (vm == null)
        {
            return null;
        }

        await _proxmoxStateService.UpdateVm(vm);
        info.Node = vm.Node;

        return vm;
    }

    public async Task<string> PowerOnVm(ProxmoxVmInfo info)
    {
        var result = await SubmitPowerOperation(info, PowerOperation.PowerOn);
        await WaitAndThrow(result, $"PowerOn vmid={info.Id}");
        _proxmoxStateService.CheckState();
        return $"vmid {info.Id} started";
    }

    public async Task<string> PowerOffVm(ProxmoxVmInfo info)
    {
        var result = await SubmitPowerOperation(info, PowerOperation.PowerOff);
        await WaitAndThrow(result, $"PowerOff vmid={info.Id}");
        _proxmoxStateService.CheckState();
        return $"vmid {info.Id} stopped";
    }

    public async Task<string> RebootVm(ProxmoxVmInfo info)
    {
        var result = await SubmitPowerOperation(info, PowerOperation.Reboot);
        await WaitAndThrow(result, $"Reboot vmid={info.Id}");
        _proxmoxStateService.CheckState();
        return $"vmid {info.Id} rebooted";
    }

    public async Task<string> ShutdownVm(ProxmoxVmInfo info)
    {
        var result = await SubmitPowerOperation(info, PowerOperation.Shutdown);
        await WaitAndThrow(result, $"Shutdown vmid={info.Id}");
        _proxmoxStateService.CheckState();
        return $"vmid {info.Id} shutdown";
    }

    public async Task<Dictionary<Guid, string>> BulkPowerOperation(Guid[] ids, PowerOperation operation)
    {
        var errors = new ConcurrentDictionary<Guid, string>();

        if (ids.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var infos = await _dbContext.ProxmoxVmInfo
            .Where(x => ids.Contains(x.VmId))
            .ToListAsync();

        foreach (var id in ids.Where(x => !infos.Any(y => y.VmId == x)))
        {
            errors.TryAdd(id, "Virtual machine not found");
        }

        // Submissions return as soon as pvedaemon accepts the task, so fan out unbounded like
        // VsphereService.BulkPowerOperation does. Completion is observed by ProxmoxTaskService.
        await Task.WhenAll(infos.Select(async info =>
        {
            try
            {
                var result = await SubmitPowerOperation(info, operation);

                if (!result.IsSuccessStatusCode)
                {
                    // The node may be stale after a migration - re-resolve it and retry once.
                    if (await ResolveNode(info) != null)
                    {
                        result = await SubmitPowerOperation(info, operation);
                    }
                }

                errors.TryAdd(info.VmId, result.IsSuccessStatusCode ? string.Empty : result.GetError());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to submit {operation} for vmid={info.Id}");
                errors.TryAdd(info.VmId, ex.Message);
            }
        }));

        _proxmoxStateService.CheckState();

        return errors.ToDictionary(x => x.Key, x => x.Value);
    }

    /// <summary>
    /// Submits a power operation and returns as soon as Proxmox accepts it, without waiting for the
    /// resulting task to finish. Callers that need the outcome pass the Result to WaitAndThrow.
    /// </summary>
    private async Task<Result> SubmitPowerOperation(ProxmoxVmInfo info, PowerOperation operation)
    {
        var lxc = info.Type == ProxmoxVmType.LXC;

        return operation switch
        {
            PowerOperation.PowerOn => lxc
                ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Start.VmStart()
                : await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Start.VmStart(),
            PowerOperation.PowerOff => lxc
                ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Stop.VmStop()
                : await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Stop.VmStop(),
            PowerOperation.Reboot => lxc
                ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Reboot.VmReboot()
                : await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Reboot.VmReboot(),
            PowerOperation.Shutdown => lxc
                ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Shutdown.VmShutdown()
                : await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Shutdown.VmShutdown(),
            _ => throw new NotSupportedException($"{operation} is not supported on Proxmox virtual machines."),
        };
    }

    public async Task<GuestProcessResult> RunGuestProcess(ProxmoxVmInfo info, string command, string arguments, TimeSpan timeout)
    {
        EnsureQemu(info, nameof(RunGuestProcess));

        var commandList = BuildAgentCommand(command, arguments);
        var execResult = await _pveClient.Nodes[info.Node].Qemu[info.Id].Agent.Exec.Exec(commandList);
        if (!execResult.IsSuccessStatusCode)
            throw new Exception($"QGA exec failed for vmid={info.Id}: {execResult.GetError()}");

        long pid = (long)execResult.Response.data.pid;
        var deadline = DateTime.UtcNow + timeout;
        var pollMs = _options.GuestProcessPollMs;

        while (true)
        {
            var statusResult = await _pveClient.Nodes[info.Node].Qemu[info.Id].Agent.ExecStatus.ExecStatus((int)pid);
            if (!statusResult.IsSuccessStatusCode)
                throw new Exception($"QGA exec-status failed for vmid={info.Id} pid={pid}: {statusResult.GetError()}");

            dynamic data = statusResult.Response.data;
            int exited = data.exited != null ? (int)data.exited : 0;

            if (exited == 1)
            {
                int exitCode = data.exitcode != null ? (int)data.exitcode : -1;
                string outData = DecodeAgentOutput(data, "out-data");
                string errData = DecodeAgentOutput(data, "err-data");

                return new GuestProcessResult
                {
                    Output = string.IsNullOrEmpty(errData) ? outData : outData + errData,
                    ExitCode = exitCode,
                    Success = exitCode == 0,
                    Error = errData
                };
            }

            if (DateTime.UtcNow >= deadline)
            {
                return new GuestProcessResult
                {
                    Output = string.Empty,
                    ExitCode = -1,
                    Success = false,
                    Error = $"QGA exec timed out after {timeout.TotalSeconds:F0}s (pid={pid})"
                };
            }

            await Task.Delay(pollMs);
        }
    }

    public async Task<long> RunGuestProcessFast(ProxmoxVmInfo info, string command, string arguments)
    {
        EnsureQemu(info, nameof(RunGuestProcessFast));

        var commandList = BuildAgentCommand(command, arguments);
        var execResult = await _pveClient.Nodes[info.Node].Qemu[info.Id].Agent.Exec.Exec(commandList);
        if (!execResult.IsSuccessStatusCode)
            throw new Exception($"QGA exec failed for vmid={info.Id}: {execResult.GetError()}");

        return (long)execResult.Response.data.pid;
    }

    public async Task<string> ReadGuestFile(ProxmoxVmInfo info, string guestFilePath)
    {
        EnsureQemu(info, nameof(ReadGuestFile));

        var result = await _pveClient.Nodes[info.Node].Qemu[info.Id].Agent.FileRead.FileRead(guestFilePath);
        if (!result.IsSuccessStatusCode)
            throw new Exception($"QGA file-read failed for vmid={info.Id} path={guestFilePath}: {result.GetError()}");

        dynamic data = result.Response.data;
        string content = data.content != null ? (string)data.content : string.Empty;
        return content;
    }

    public async Task<string> UploadFileToGuest(ProxmoxVmInfo info, string guestFilePath, Stream content)
    {
        EnsureQemu(info, nameof(UploadFileToGuest));

        var maxBytes = _options.FileUploadMaxBytes;
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms);
        var buffer = ms.ToArray();
        if (buffer.Length > maxBytes)
            throw new InvalidOperationException(
                $"QGA file-write payload {buffer.Length} bytes exceeds {maxBytes}-byte limit. Use vSphere upload or chunk uploads via guest exec.");

        var text = System.Text.Encoding.UTF8.GetString(buffer);
        var result = await _pveClient.Nodes[info.Node].Qemu[info.Id].Agent.FileWrite.FileWrite(text, guestFilePath);
        if (!result.IsSuccessStatusCode)
            throw new Exception($"QGA file-write failed for vmid={info.Id} path={guestFilePath}: {result.GetError()}");

        return $"wrote {buffer.Length} bytes to {guestFilePath} on vmid={info.Id}";
    }

    public async Task<List<ProxmoxSnapshot>> GetSnapshots(ProxmoxVmInfo info)
    {
        var result = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Snapshot.List()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].Snapshot.SnapshotList();

        if (!result.IsSuccessStatusCode)
            throw new Exception($"SnapshotList failed for vmid={info.Id}: {result.GetError()}");

        var list = new List<ProxmoxSnapshot>();
        foreach (var entry in result.ToData())
        {
            dynamic d = entry;
            string name = d.name != null ? (string)d.name : null;
            if (string.IsNullOrEmpty(name)) continue;
            // The API includes a synthetic "current" entry; surface it so the caller can detect it,
            // matching how Proxmox UI lists snapshots.
            list.Add(new ProxmoxSnapshot
            {
                Name = name,
                Description = d.description != null ? (string)d.description : null,
                Parent = d.parent != null ? (string)d.parent : null,
                VmState = d.vmstate != null && (int)d.vmstate == 1,
                SnapTime = d.snaptime != null ? (long?)d.snaptime : null
            });
        }
        return list;
    }

    public async Task<string> CreateSnapshot(ProxmoxVmInfo info, string snapshotName, string description, bool includeRam)
    {
        Result result;
        if (info.Type == ProxmoxVmType.LXC)
        {
            // LXC snapshots don't capture VM RAM; the includeRam flag is ignored.
            result = await _pveClient.Nodes[info.Node].Lxc[info.Id].Snapshot.Snapshot(snapshotName, description);
        }
        else
        {
            result = await _pveClient.Nodes[info.Node].Qemu[info.Id].Snapshot.Snapshot(snapshotName, description, includeRam);
        }

        await WaitAndThrow(result, $"CreateSnapshot vmid={info.Id} name={snapshotName}");
        return $"snapshot {snapshotName} created on vmid {info.Id}";
    }

    public async Task<string> RevertSnapshot(ProxmoxVmInfo info, string snapshotName)
    {
        var result = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Snapshot[snapshotName].Rollback.Rollback()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].Snapshot[snapshotName].Rollback.Rollback();

        await WaitAndThrow(result, $"RevertSnapshot vmid={info.Id} name={snapshotName}");
        _proxmoxStateService.CheckState();
        return $"snapshot {snapshotName} restored on vmid {info.Id}";
    }

    public async Task<string> DeleteSnapshot(ProxmoxVmInfo info, string snapshotName)
    {
        var result = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Snapshot[snapshotName].Delsnapshot()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].Snapshot[snapshotName].Delsnapshot();

        await WaitAndThrow(result, $"DeleteSnapshot vmid={info.Id} name={snapshotName}");
        return $"snapshot {snapshotName} deleted on vmid {info.Id}";
    }

    #region View/team-scoped ISO storage

    public async Task<IReadOnlyList<ProxmoxIsoVolume>> ListStorageIsos(CancellationToken cancellationToken)
    {
        // Keyed on the storage name rather than a filename: a whole-storage listing has no one file to
        // be affine to, and this keeps repeated listings hitting the same node (and so the same cache).
        return await OnIsoStorageNode(
            _options.IsoStorage,
            "ListStorageIsos",
            node => ListIsosOnNode(node, cancellationToken));
    }

    public async Task<IReadOnlyList<ProxmoxIsoVolume>> ListStorageIsosForVm(Guid vmId, CancellationToken cancellationToken)
    {
        EnsureIsoStorageConfigured();

        var vm = await _dbContext.Vms
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == vmId, cancellationToken);

        if (vm?.ProxmoxVmInfo == null)
            return [];

        // Deliberately no failover across nodes here: the point of this listing is that every volume id
        // it returns is mountable by THIS Vm, which on a non-shared storage is only true of the node the
        // Vm is on. ResolveNode also refreshes info.Node, which goes stale on migration.
        var resource = await ResolveNode(vm.ProxmoxVmInfo);

        if (resource == null)
            throw new InvalidOperationException($"Could not find vmid {vm.ProxmoxVmInfo.Id} in Proxmox");

        return await ListIsosOnNode(vm.ProxmoxVmInfo.Node, cancellationToken);
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

    public async Task UploadIsoToStorage(string encodedFileName, string localFilePath, CancellationToken cancellationToken)
    {
        await OnIsoStorageNode(
            encodedFileName,
            $"UploadIsoToStorage file={encodedFileName}",
            async node =>
            {
                // Both the stream and the client are created inside the per-node action, not outside
                // it: a failover to the next node has to start the body from the beginning rather
                // than from wherever the failed attempt left the stream, and it needs an HttpClient
                // that has not yet sent a request (see CreateUploadClient).
                using var fileStream = File.OpenRead(localFilePath);

                var result = await CreateUploadClient().UploadFileToStorageAsync(
                    node,
                    _options.IsoStorage,
                    IsoContentType,
                    fileStream,
                    encodedFileName,
                    cancellationToken,
                    secondsTimeout: _uploadSecondsTimeout);

                await WaitAndThrow(result, $"UploadIsoToStorage node={node} file={encodedFileName}", cancellationToken);
                return true;
            });
    }

    public async Task DeleteIsoFromStorage(string encodedFileName, CancellationToken cancellationToken)
    {
        var volumeId = ProxmoxIsoNaming.BuildVolumeId(_options.IsoStorage, encodedFileName);

        await OnIsoStorageNode(
            encodedFileName,
            $"DeleteIsoFromStorage file={encodedFileName}",
            async node =>
            {
                var result = await _pveClient.Nodes[node].Storage[_options.IsoStorage].Content[volumeId].Delete();

                // Idempotent: a file that is already gone is the state the caller asked for. PVE reports
                // this as a 500 with "does not exist" rather than a 404, so the message is all there is
                // to match on.
                if (!result.IsSuccessStatusCode)
                {
                    var error = result.GetError() ?? string.Empty;

                    if (error.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                        error.Contains("no such file", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "ISO {File} was already absent from Proxmox storage {Storage} on node {Node}",
                            encodedFileName, _options.IsoStorage, node);
                        return true;
                    }
                }

                await WaitAndThrow(result, $"DeleteIsoFromStorage node={node} file={encodedFileName}", cancellationToken);
                return true;
            });
    }

    public async Task MountIso(ProxmoxVmInfo info, string isoVolumeId, CancellationToken cancellationToken)
    {
        // BadRequest rather than EnsureQemu's InvalidOperationException: an LXC container has no CD-ROM
        // to swap, which is a property of the request, not a server fault.
        if (info.Type != ProxmoxVmType.QEMU)
            throw new BadRequestException("Mounting an ISO is only supported on QEMU virtual machines.");

        var resource = await ResolveNode(info);

        if (resource == null)
            throw new InvalidOperationException($"Could not find vmid {info.Id} in Proxmox");

        var config = await _pveClient.Nodes[info.Node].Qemu[info.Id].Config.GetAsync(true);

        // DisksAll, not Disks: CD-ROM drives are excluded from Disks.
        var drives = (config.DisksAll ?? [])
            .Where(x => x.Kind == VmDiskKind.Cdrom && !string.IsNullOrWhiteSpace(x.Id))
            .ToList();

        if (drives.Count == 0)
        {
            // Not auto-added on purpose. QEMU cannot hot-add an IDE drive, so adding ide2 to a running
            // Vm lands in the pending config and appears to silently do nothing until a power cycle -
            // worse than a clear error.
            throw new BadRequestException(
                $"vmid {info.Id} has no CD/DVD drive. Add one to the VM in Proxmox before mounting an ISO.");
        }

        // ide2 is the Proxmox convention for the optical drive and is what its own UI targets; anything
        // else is only reached when a Vm was built differently.
        var target = drives.FirstOrDefault(x => string.Equals(x.Id, "ide2", StringComparison.OrdinalIgnoreCase))
            ?? drives.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).First();

        var (bus, index) = ParseDriveId(target.Id);
        var assignments = new Dictionary<int, string> { [index] = ReplaceCdromVolume(target.RawDefinition, isoVolumeId) };
        var vmConfig = _pveClient.Nodes[info.Node].Qemu[info.Id].Config;

        // Dispatched on the drive's actual bus rather than through the API's "cdrom" parameter, which is
        // documented as an alias for -ide2 and would therefore retarget a Vm whose drive is sata0.
        var result = bus switch
        {
            "ide" => await vmConfig.UpdateVmAsync(ideN: assignments),
            "sata" => await vmConfig.UpdateVmAsync(sataN: assignments),
            "scsi" => await vmConfig.UpdateVmAsync(scsiN: assignments),
            _ => throw new BadRequestException(
                $"The CD/DVD drive '{target.Id}' on vmid {info.Id} is on a bus that cannot be reconfigured.")
        };

        await WaitAndThrow(result, $"MountIso vmid={info.Id} drive={target.Id}", cancellationToken);
        _proxmoxStateService.CheckState();
    }

    /// <summary>
    /// Rebuilds a CD-ROM drive definition around a new volume, preserving the flags already on it.
    /// </summary>
    /// <remarks>
    /// Built from the existing RawDefinition rather than as "{volid},media=cdrom" so that flags like
    /// "ro" survive - the same approach <see cref="ReplaceBridge"/> takes for network adapters. The one
    /// token deliberately dropped is "size=", which PVE appends itself and which describes the medium
    /// being replaced; carrying it over to a different ISO would be wrong. PVE re-derives it.
    /// </remarks>
    internal static string ReplaceCdromVolume(string rawDefinition, string isoVolumeId)
    {
        if (string.IsNullOrWhiteSpace(rawDefinition))
            return $"{isoVolumeId},media=cdrom";

        var parts = rawDefinition.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        // The volume is the only positional token; everything else is key=value.
        parts[0] = isoVolumeId;

        parts.RemoveAll(x => x.StartsWith("size=", StringComparison.OrdinalIgnoreCase));

        if (!parts.Any(x => string.Equals(x, "media=cdrom", StringComparison.OrdinalIgnoreCase)))
            parts.Add("media=cdrom");

        return string.Join(',', parts);
    }

    /// <summary>
    /// Splits a drive key such as "ide2" or "sata0" into its bus and index, which is the shape the
    /// config update's per-bus dictionaries take.
    /// </summary>
    internal static (string Bus, int Index) ParseDriveId(string driveId)
    {
        var match = DriveIdRegex.Match(driveId ?? string.Empty);

        if (!match.Success)
            throw new BadRequestException($"Unrecognized drive id '{driveId}'.");

        return (match.Groups[1].Value.ToLowerInvariant(),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Nodes that currently offer the configured ISO storage, in the order they should be tried.
    /// </summary>
    /// <remarks>
    /// A list rather than one node, because a single sick-but-online node would otherwise break every
    /// ISO operation in the install. The order is deterministic per <paramref name="affinityKey"/>:
    /// nodes sorted by name, then rotated by a stable hash of the key. That spreads concurrent uploads
    /// across the cluster without making any one file's node choice random - so a retry lands on the
    /// same node as the attempt it is retrying, and the logs are reproducible.
    /// </remarks>
    private async Task<IReadOnlyList<string>> ResolveIsoStorageNodes(string affinityKey)
    {
        EnsureIsoStorageConfigured();

        if (!string.IsNullOrWhiteSpace(_options.IsoNode))
            return [_options.IsoNode];

        // One cluster-wide call rather than a storage query per node. Note GetResourcesAsync returns a
        // single ClusterResource type that implements every resource interface, so the resource kind has
        // to be filtered on ResourceType - an OfType<IClusterResourceStorage>() would match everything.
        var resources = await _pveClient.GetResourcesAsync(ClusterResourceType.Storage);

        var candidates = resources
            .Where(x => x.ResourceType == ClusterResourceType.Storage
                && string.Equals(x.Storage, _options.IsoStorage, StringComparison.Ordinal)
                && x.IsAvailable
                && !string.IsNullOrWhiteSpace(x.Node))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No online Proxmox node currently offers ISO storage '{_options.IsoStorage}'.");
        }

        if (!candidates[0].Shared)
        {
            // On a node-local storage an ISO uploaded via node A is invisible to a Vm on node B, and
            // failing an upload over to a second node scatters one View's ISOs across nodes. Nothing
            // here can fix that, but it should be loud in the logs when it happens.
            _logger.LogWarning(
                "Proxmox ISO storage {Storage} is not shared. ISOs will only be mountable by VMs on the node they were uploaded to; configure Proxmox.IsoNode to pin every operation to one node.",
                _options.IsoStorage);
        }

        var ordered = candidates
            .Select(x => x.Node)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var start = StableIndex(affinityKey, ordered.Count);

        return [.. ordered.Skip(start), .. ordered.Take(start)];
    }

    /// <summary>
    /// Runs a storage operation on the first candidate node that accepts it, logging and moving on when
    /// one fails.
    /// </summary>
    private async Task<T> OnIsoStorageNode<T>(string affinityKey, string operation, Func<string, Task<T>> action)
    {
        var nodes = await ResolveIsoStorageNodes(affinityKey);
        Exception lastError = null;

        foreach (var node in nodes)
        {
            try
            {
                return await action(node);
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex,
                    "{Operation} failed on Proxmox node {Node}; trying the next node offering storage {Storage}",
                    operation, node, _options.IsoStorage);
            }
        }

        throw new Exception(
            $"{operation} failed on every Proxmox node offering ISO storage '{_options.IsoStorage}'.",
            lastError);
    }

    private void EnsureIsoStorageConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.IsoStorage))
            throw new InvalidOperationException("Proxmox ISO support requires Proxmox:IsoStorage to be set.");
    }

    /// <summary>
    /// A stable index in [0, count) derived from a string. FNV-1a rather than string.GetHashCode,
    /// which is randomized per process and would therefore pick a different node on every restart.
    /// </summary>
    private static int StableIndex(string key, int count)
    {
        if (count <= 1 || string.IsNullOrEmpty(key))
            return 0;

        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            var hash = offsetBasis;
            foreach (var c in key)
            {
                hash ^= c;
                hash *= prime;
            }

            return (int)(hash % (uint)count);
        }
    }

    #endregion

    private async Task WaitAndThrow(
        Result result,
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (!result.IsSuccessStatusCode)
            throw new Exception($"{operation} failed: {result.GetError()}");

        var finished = await Extensions.ProxmoxExtensions.WaitForTaskToFinish(
            _pveClient,
            result,
            cancellationToken: cancellationToken);
        if (!finished)
            throw new TimeoutException($"{operation} timed out waiting for the Proxmox task to finish.");
    }

    private static void EnsureQemu(ProxmoxVmInfo info, string operation)
    {
        if (info.Type != ProxmoxVmType.QEMU)
            throw new InvalidOperationException(
                $"{operation} is only supported on QEMU VMs (vmid={info.Id} is {info.Type}).");
    }

    private static List<object> BuildAgentCommand(string command, string arguments)
    {
        // QGA exec takes the program in its first slot followed by separate arguments. We tokenize
        // the single 'arguments' string with shell-style quote/escape handling so callers can
        // safely write things like:  -c "touch /tmp/x.txt"  or  -c 'echo "hi there"'.
        var list = new List<object> { command };
        if (!string.IsNullOrEmpty(arguments))
        {
            foreach (var arg in TokenizeShellArguments(arguments))
                list.Add(arg);
        }
        return list;
    }

    /// <summary>
    /// Splits a command-line argument string the way POSIX shells do: whitespace separates tokens
    /// except inside single or double quotes, and a backslash escapes the next character outside
    /// single quotes. Mismatched quotes throw — better to fail loudly than silently mis-execute.
    /// </summary>
    private static List<string> TokenizeShellArguments(string input)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var hasToken = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (inSingle)
            {
                if (c == '\'') { inSingle = false; }
                else { current.Append(c); }
                continue;
            }

            if (inDouble)
            {
                if (c == '\\' && i + 1 < input.Length)
                {
                    var next = input[i + 1];
                    // Inside double quotes only \, ", $, ` and newline are escapable; other
                    // backslashes are kept literally (mirrors bash behavior closely enough).
                    if (next == '\\' || next == '"' || next == '$' || next == '`' || next == '\n')
                    {
                        current.Append(next);
                        i++;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c == '"') { inDouble = false; }
                else { current.Append(c); }
                continue;
            }

            if (c == '\'') { inSingle = true; hasToken = true; continue; }
            if (c == '"') { inDouble = true; hasToken = true; continue; }
            if (c == '\\' && i + 1 < input.Length) { current.Append(input[++i]); hasToken = true; continue; }

            if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (inSingle || inDouble)
            throw new ArgumentException("Unterminated quote in arguments string.");

        if (hasToken)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static string DecodeAgentOutput(dynamic data, string field)
    {
        // QGA returns "out-data" / "err-data" as plain strings (not base64) — only the truncated
        // flag indicates capture limits. Fall back to empty when absent. The PveClient surfaces
        // the JSON object as an ExpandoObject, so we look up the hyphenated key via its
        // IDictionary<string, object> view rather than dynamic [] / member access.
        if (data is IDictionary<string, object> dict && dict.TryGetValue(field, out var raw) && raw != null)
            return raw.ToString();
        return string.Empty;
    }
}
