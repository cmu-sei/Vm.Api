// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Extension;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;

namespace Player.Vm.Api.Domain.Proxmox.Services;

public interface IProxmoxService
{
    Task<ProxmoxConsole> GetConsole(ProxmoxVmInfo info);
    Task<IEnumerable<IClusterResourceVm>> GetVms();

    Task<string> PowerOnVm(ProxmoxVmInfo info);
    Task<string> PowerOffVm(ProxmoxVmInfo info);
    Task<string> RebootVm(ProxmoxVmInfo info);
    Task<string> ShutdownVm(ProxmoxVmInfo info);

    Task<GuestProcessResult> RunGuestProcessAsync(ProxmoxVmInfo info, string command, string arguments, TimeSpan timeout);
    Task<long> RunGuestProcessFastAsync(ProxmoxVmInfo info, string command, string arguments);
    Task<string> ReadGuestFileAsync(ProxmoxVmInfo info, string guestFilePath);
    Task<string> UploadFileToGuestAsync(ProxmoxVmInfo info, string guestFilePath, Stream content);

    Task<int> CloneVmFromTemplateAsync(ProxmoxVmInfo sourceInfo, string cloneName, bool powerOn);
    Task<string> DeleteVmAsync(ProxmoxVmInfo info);

    Task<List<ProxmoxSnapshot>> GetSnapshots(ProxmoxVmInfo info);
    Task<string> CreateSnapshotAsync(ProxmoxVmInfo info, string snapshotName, string description, bool includeRam);
    Task<string> RevertSnapshotAsync(ProxmoxVmInfo info, string snapshotName);
    Task<string> DeleteSnapshotAsync(ProxmoxVmInfo info, string snapshotName);
}

public class ProxmoxService : IProxmoxService
{
    private readonly ProxmoxOptions _options;
    private readonly ILogger<ProxmoxService> _logger;
    private readonly PveClient _pveClient;
    private readonly IProxmoxStateService _proxmoxStateService;
    private readonly RewriteHostOptions _rewriteHostOptions;

    public ProxmoxService(
            ProxmoxOptions options,
            ILogger<ProxmoxService> logger,
            IProxmoxStateService proxmoxStateService,
            RewriteHostOptions rewriteHostOptions
        )
    {
        _options = options;
        _logger = logger;
        _proxmoxStateService = proxmoxStateService;
        _rewriteHostOptions = rewriteHostOptions;

        _pveClient = new PveClient(options.Host, _options.Port);
        _pveClient.ApiToken = options.Token;
    }

    public async Task<ProxmoxConsole> GetConsole(ProxmoxVmInfo info)
    {
        var result = await VncProxyCall(info.Node, info.Id, info.Type);
        var success = result.IsSuccessStatusCode;

        if (!success)
        {
            // Check if vm exists on a different node and try again
            var vm = await _pveClient.GetVmAsync(info.Id);

            if (vm != null)
            {
                await _proxmoxStateService.UpdateVm(vm);

                if (vm.IsRunning)
                {
                    info.Node = vm.Node;
                    result = await VncProxyCall(info.Node, info.Id, info.Type);
                    success = result.IsSuccessStatusCode;
                }
            }

            if (!success)
            {
                throw new Exception(result.GetError());
            }
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
            Url = url
        };
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

    private async Task<IClusterResourceVm> RefreshVm(int id)
    {
        var vm = await _pveClient.GetVmAsync(id);

        return vm;
    }

    public async Task<string> PowerOnVm(ProxmoxVmInfo info)
    {
        var result = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Start.VmStart()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Start.VmStart();

        await WaitAndThrow(result, $"PowerOn vmid={info.Id}");
        _proxmoxStateService.CheckState();
        return $"vmid {info.Id} started";
    }

    public async Task<string> PowerOffVm(ProxmoxVmInfo info)
    {
        var result = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Stop.VmStop()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Stop.VmStop();

        await WaitAndThrow(result, $"PowerOff vmid={info.Id}");
        _proxmoxStateService.CheckState();
        return $"vmid {info.Id} stopped";
    }

    public async Task<string> RebootVm(ProxmoxVmInfo info)
    {
        if (info.Type == ProxmoxVmType.LXC)
        {
            // LXC has no reboot endpoint; emulate via stop + start
            var stop = await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Stop.VmStop();
            await WaitAndThrow(stop, $"Reboot/stop vmid={info.Id}");
            var start = await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Start.VmStart();
            await WaitAndThrow(start, $"Reboot/start vmid={info.Id}");
        }
        else
        {
            var result = await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Reboot.VmReboot();
            await WaitAndThrow(result, $"Reboot vmid={info.Id}");
        }

        _proxmoxStateService.CheckState();
        return $"vmid {info.Id} rebooted";
    }

    public async Task<string> ShutdownVm(ProxmoxVmInfo info)
    {
        var result = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Shutdown.VmShutdown()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Shutdown.VmShutdown();

        await WaitAndThrow(result, $"Shutdown vmid={info.Id}");
        _proxmoxStateService.CheckState();
        return $"vmid {info.Id} shutdown";
    }

    public async Task<GuestProcessResult> RunGuestProcessAsync(ProxmoxVmInfo info, string command, string arguments, TimeSpan timeout)
    {
        EnsureQemu(info, nameof(RunGuestProcessAsync));

        var commandList = BuildAgentCommand(command, arguments);
        var execResult = await _pveClient.Nodes[info.Node].Qemu[info.Id].Agent.Exec.Exec(commandList);
        if (!execResult.IsSuccessStatusCode)
            throw new Exception($"QGA exec failed for vmid={info.Id}: {execResult.GetError()}");

        long pid = (long)execResult.Response.data.pid;
        var deadline = DateTime.UtcNow + timeout;
        const int pollMs = 500;

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

    public async Task<long> RunGuestProcessFastAsync(ProxmoxVmInfo info, string command, string arguments)
    {
        EnsureQemu(info, nameof(RunGuestProcessFastAsync));

        var commandList = BuildAgentCommand(command, arguments);
        var execResult = await _pveClient.Nodes[info.Node].Qemu[info.Id].Agent.Exec.Exec(commandList);
        if (!execResult.IsSuccessStatusCode)
            throw new Exception($"QGA exec failed for vmid={info.Id}: {execResult.GetError()}");

        return (long)execResult.Response.data.pid;
    }

    public async Task<string> ReadGuestFileAsync(ProxmoxVmInfo info, string guestFilePath)
    {
        EnsureQemu(info, nameof(ReadGuestFileAsync));

        var result = await _pveClient.Nodes[info.Node].Qemu[info.Id].Agent.FileRead.FileRead(guestFilePath);
        if (!result.IsSuccessStatusCode)
            throw new Exception($"QGA file-read failed for vmid={info.Id} path={guestFilePath}: {result.GetError()}");

        dynamic data = result.Response.data;
        string content = data.content != null ? (string)data.content : string.Empty;
        return content;
    }

    public async Task<string> UploadFileToGuestAsync(ProxmoxVmInfo info, string guestFilePath, Stream content)
    {
        EnsureQemu(info, nameof(UploadFileToGuestAsync));

        const int maxBytes = 60 * 1024;
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

    public async Task<int> CloneVmFromTemplateAsync(ProxmoxVmInfo sourceInfo, string cloneName, bool powerOn)
    {
        EnsureQemu(sourceInfo, nameof(CloneVmFromTemplateAsync));

        var nextIdResult = await _pveClient.Cluster.Nextid.Nextid();
        if (!nextIdResult.IsSuccessStatusCode)
            throw new Exception($"Cluster.Nextid failed: {nextIdResult.GetError()}");

        int newId = int.Parse((string)nextIdResult.Response.data);

        var cloneResult = await _pveClient.Nodes[sourceInfo.Node].Qemu[sourceInfo.Id].Clone.CloneVm(
            newid: newId,
            name: cloneName,
            full: true);
        await WaitAndThrow(cloneResult, $"Clone vmid={sourceInfo.Id} -> {newId}");

        if (powerOn)
        {
            var startResult = await _pveClient.Nodes[sourceInfo.Node].Qemu[newId].Status.Start.VmStart();
            await WaitAndThrow(startResult, $"PowerOn cloned vmid={newId}");
        }

        _proxmoxStateService.CheckState();
        return newId;
    }

    public async Task<string> DeleteVmAsync(ProxmoxVmInfo info)
    {
        Result task;

        var statusResult = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Current.VmStatus()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Current.VmStatus();

        bool isRunning = statusResult.IsSuccessStatusCode &&
            ((string)statusResult.Response.data.status) == "running";

        if (isRunning)
        {
            task = info.Type == ProxmoxVmType.LXC
                ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Status.Stop.VmStop()
                : await _pveClient.Nodes[info.Node].Qemu[info.Id].Status.Stop.VmStop();
            await WaitAndThrow(task, $"Stop before delete vmid={info.Id}");
        }

        task = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].DestroyVm()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].DestroyVm();
        await WaitAndThrow(task, $"Destroy vmid={info.Id}");

        _proxmoxStateService.CheckState();
        return $"vmid {info.Id} destroyed";
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

    public async Task<string> CreateSnapshotAsync(ProxmoxVmInfo info, string snapshotName, string description, bool includeRam)
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

    public async Task<string> RevertSnapshotAsync(ProxmoxVmInfo info, string snapshotName)
    {
        var result = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Snapshot[snapshotName].Rollback.Rollback()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].Snapshot[snapshotName].Rollback.Rollback();

        await WaitAndThrow(result, $"RevertSnapshot vmid={info.Id} name={snapshotName}");
        _proxmoxStateService.CheckState();
        return $"snapshot {snapshotName} restored on vmid {info.Id}";
    }

    public async Task<string> DeleteSnapshotAsync(ProxmoxVmInfo info, string snapshotName)
    {
        var result = info.Type == ProxmoxVmType.LXC
            ? await _pveClient.Nodes[info.Node].Lxc[info.Id].Snapshot[snapshotName].Delsnapshot()
            : await _pveClient.Nodes[info.Node].Qemu[info.Id].Snapshot[snapshotName].Delsnapshot();

        await WaitAndThrow(result, $"DeleteSnapshot vmid={info.Id} name={snapshotName}");
        return $"snapshot {snapshotName} deleted on vmid {info.Id}";
    }

    private async Task WaitAndThrow(Result result, string operation)
    {
        if (!result.IsSuccessStatusCode)
            throw new Exception($"{operation} failed: {result.GetError()}");

        await _pveClient.WaitForTaskToFinishAsync(result);
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
