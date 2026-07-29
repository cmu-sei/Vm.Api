// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Node;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Infrastructure.Extensions;

namespace Player.Vm.Api.Domain.Proxmox.Services;

public interface IProxmoxTaskService
{
    void CheckTasks();
}

/// <summary>
/// The Proxmox counterpart of <see cref="Vsphere.Services.TaskService"/>. Polls cluster-wide tasks
/// and reconciles Vm.HasPendingTasks from them, so a power operation started anywhere - this API,
/// the PVE web UI, or the qm CLI - shows as pending in the UI. Because it is the single source of
/// truth for pending state, IProxmoxService does not need to hand off the UPIDs it submits.
/// </summary>
public class ProxmoxTaskService : BackgroundService, IProxmoxTaskService
{
    private readonly ILogger<ProxmoxTaskService> _logger;
    private readonly IOptionsMonitor<ProxmoxOptions> _optionsMonitor;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<ProgressHub> _progressHub;
    private readonly ConcurrentDictionary<string, List<Notification>> _runningTasks = new();
    private readonly AsyncAutoResetEvent _resetEvent = new(false);
    private bool _tasksPending = false;

    /// <summary>
    /// PVE models an interactive console session as a task that stays running for the life of the
    /// session, and those tasks carry a real vmid. Treating them as pending work would leave every
    /// Vm with an open console permanently flagged - and would hold the poller at its ReCheck
    /// interval forever. vSphere needs no equivalent list because vCenter does not report console
    /// sessions as recent tasks.
    /// </summary>
    private static readonly HashSet<string> SessionTaskTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "vncproxy",
        "spiceproxy",
        "termproxy",
        "vncshell",
        "spiceshell",
    };

    /// <summary>
    /// UPIDs of finished-unsuccessfully tasks already logged. Cluster/tasks keeps returning a failed
    /// task for as long as PVE retains it, so without this the same failure is logged every poll.
    /// Pruned each pass to the UPIDs PVE still reports, which bounds it to the task list size.
    /// </summary>
    private readonly HashSet<string> _loggedFailures = [];

    public ProxmoxTaskService(
            ILogger<ProxmoxTaskService> logger,
            IOptionsMonitor<ProxmoxOptions> optionsMonitor,
            IServiceProvider serviceProvider,
            IHubContext<ProgressHub> progressHub
        )
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _serviceProvider = serviceProvider;
        _progressHub = progressHub;
    }

    public void CheckTasks()
    {
        _resetEvent.Set();
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _tasksPending = false;

                if (_optionsMonitor.CurrentValue.Enabled)
                {
                    using var scope = _serviceProvider.CreateScope();
                    await ProcessTasks(
                        scope.ServiceProvider.GetRequiredService<VmContext>(),
                        scope.ServiceProvider.GetRequiredService<IProxmoxService>(),
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in {nameof(ProxmoxTaskService)}");
            }

            var intervalMilliseconds = _tasksPending
                ? _optionsMonitor.CurrentValue.ReCheckTaskProgressIntervalMilliseconds
                : _optionsMonitor.CurrentValue.CheckTaskProgressIntervalMilliseconds;

            await _resetEvent.WaitAsync(new TimeSpan(0, 0, 0, 0, intervalMilliseconds), cancellationToken);
        }
    }

    private async Task ProcessTasks(VmContext dbContext, IProxmoxService proxmoxService, CancellationToken cancellationToken)
    {
        await GetRecentTasks(dbContext, proxmoxService, cancellationToken);

        foreach (var vmTasks in _runningTasks)
        {
            try
            {
                await _progressHub.Clients.Group(vmTasks.Key).SendAsync("Progress", vmTasks.Value, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception broadcasting Proxmox progress for {vmTasks.Key}");
            }
        }
    }

    private async Task GetRecentTasks(VmContext dbContext, IProxmoxService proxmoxService, CancellationToken cancellationToken)
    {
        var tasks = (await proxmoxService.GetTasks()).ToList();

        // Forget failures PVE has aged out, so the set tracks the current task list rather than
        // growing for the life of the process.
        _loggedFailures.IntersectWith(tasks.Select(x => x.UniqueTaskId));

        // vmid -> Vm.Id, the analogue of IConnectionService.GetVmIdByRef.
        var vmIdsByVmid = await dbContext.ProxmoxVmInfo
            .ToDictionaryAsync(x => x.Id, x => x.VmId, cancellationToken);

        _runningTasks.Clear();
        var stillPendingVmIds = new List<Guid>();

        foreach (var task in tasks)
        {
            try
            {
                // NodeTask.VmId is PVE's generic entity id, so it is not always a vmid - a backup
                // task reports something like "local:backup". Failing to parse it drops both those
                // tasks and any vmid Player does not track.
                if (!int.TryParse(task.VmId, out var vmid) ||
                    !vmIdsByVmid.TryGetValue(vmid, out var vmId) ||
                    SessionTaskTypes.Contains(task.Type))
                {
                    continue;
                }

                // EndTime is a non-nullable long that is 0 until the task finishes; Duration is the
                // library's nullable view of the same thing and reads more clearly.
                var running = !task.Duration.HasValue;

                _runningTasks.AddOrUpdate(
                    vmId.ToString(),
                    _ => [ToNotification(task, running)],
                    (_, existing) => [.. existing, ToNotification(task, running)]);

                if (running)
                {
                    _tasksPending = true;
                    stillPendingVmIds.Add(vmId);
                }
                else if (!task.StatusOk && _loggedFailures.Add(task.UniqueTaskId))
                {
                    // StatusOk is false for both failures and still-running tasks, so it is only a
                    // meaningful error signal once the task is known to have finished. This is the
                    // error channel for bulk operations, which do not wait on their tasks.
                    _logger.LogWarning($"Proxmox task {task.UniqueTaskId} for vmid={vmid} finished unsuccessfully: {task.Status}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception processing Proxmox task {task?.UniqueTaskId}");
            }
        }

        var pendingVms = await dbContext.Vms
            .Include(x => x.VmTeams)
            .Where(x => x.HasPendingTasks && x.Type == Domain.Models.VmType.Proxmox)
            .ToArrayAsync(cancellationToken);

        foreach (var vm in pendingVms)
        {
            if (!stillPendingVmIds.Contains(vm.Id))
            {
                vm.HasPendingTasks = false;
            }
        }

        if (stillPendingVmIds.Count > 0)
        {
            var vmsToUpdate = await dbContext.Vms
                .Include(x => x.VmTeams)
                .Where(x => stillPendingVmIds.Contains(x.Id))
                .ToArrayAsync(cancellationToken);

            foreach (var vm in vmsToUpdate)
            {
                vm.HasPendingTasks = true;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Notification ToNotification(NodeTask task, bool running)
    {
        return new Notification
        {
            broadcastTime = DateTime.UtcNow.ToString(),
            taskId = task.UniqueTaskId,
            // e.g. "VM 101 Start", the counterpart of vSphere's descriptionId with the prefix stripped.
            taskName = task.Description,
            taskType = task.Type,
            progress = string.Empty,
            state = running ? "running" : task.Status
        };
    }
}
