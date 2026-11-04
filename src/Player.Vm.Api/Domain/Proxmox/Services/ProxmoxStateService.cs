// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Data;
using Nito.AsyncEx;
using Player.Vm.Api.Infrastructure.Extensions;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Player.Vm.Api.Domain.Proxmox.Extensions;
using System.Threading.Tasks.Dataflow;
using Player.Vm.Api.Domain.Proxmox.Options;
using Microsoft.Extensions.Options;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Models;

namespace Player.Vm.Api.Domain.Proxmox.Services;

public interface IProxmoxStateService
{
    void CheckState();
    Task UpdateVm(IClusterResourceVm vm);

    /// <summary>
    /// Stores whether a Vm has a snapshot to revert to, for a caller that has just changed it and
    /// cannot wait for the next guest-state read.
    /// </summary>
    Task SetHasSnapshot(Guid vmId, bool hasSnapshot);
}

public class ProxmoxStateService : BackgroundService, IProxmoxStateService
{
    private readonly ILogger<ProxmoxStateService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly AsyncAutoResetEvent _resetEvent = new AsyncAutoResetEvent(false);
    private readonly ActionBlock<IClusterResourceVm> _jobQueue;
    private readonly IOptionsMonitor<ProxmoxOptions> _proxmoxOptionsMonitor;

    private const int MinimumIntervalSeconds = 1;

    /// <summary>
    /// The last out-of-range interval warned about, so a standing misconfiguration is reported once
    /// rather than on every pass. Reset when the value changes, so a re-edit is warned about again.
    /// </summary>
    private int? _warnedIntervalSeconds;

    /// <summary>
    /// When guest state was last read, so it can be read on its own, slower interval: it costs a request
    /// or two per tracked Vm, where the power-state read is one request for the whole cluster.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _sinceGuestStateRead = new();

    /// <summary>
    /// The guest-state read in progress, if any. It runs beside the power-state passes rather than
    /// inside one: it costs up to GuestAgentTimeoutMilliseconds per machine whose agent does not answer,
    /// and power state must not wait on that. At most one runs at a time.
    /// </summary>
    private Task _guestStateRead = Task.CompletedTask;

    /// <summary>
    /// Cancels a guest-state read only when stopping has run out of time. Not the loop's token: a
    /// graceful stop lets the read in progress finish and save rather than throwing its work away.
    /// </summary>
    private readonly CancellationTokenSource _guestStateCancellation = new();

    public ProxmoxStateService(
            ILogger<ProxmoxStateService> logger,
            IOptionsMonitor<ProxmoxOptions> proxmoxOptionsMonitor,
            IServiceProvider serviceProvider
        )
    {
        _logger = logger;
        _proxmoxOptionsMonitor = proxmoxOptionsMonitor;
        _serviceProvider = serviceProvider;

        _jobQueue = new ActionBlock<IClusterResourceVm>(
               async pveVm => await ProcessVm(pveVm),
               new ExecutionDataflowBlockOptions
               {
                   MaxDegreeOfParallelism = -1
               }
           );
    }

    /// <summary>
    /// Brings the next power-state pass forward. Does not force a guest-state read: a power operation
    /// or revert changes neither HasSnapshot nor, until the guest has booted, its addresses, and forcing
    /// one would read every tracked machine to update one. Snapshot changes record HasSnapshot through
    /// <see cref="SetHasSnapshot"/> instead.
    /// </summary>
    public void CheckState()
    {
        _resetEvent.Set();
    }

    public async Task UpdateVm(IClusterResourceVm vm)
    {
        await _jobQueue.SendAsync(vm);
    }

    public async Task SetHasSnapshot(Guid vmId, bool hasSnapshot)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<VmContext>();
        var dbVm = await dbContext.Vms.FirstOrDefaultAsync(x => x.Id == vmId);

        if (dbVm == null || dbVm.HasSnapshot == hasSnapshot)
            return;

        dbVm.HasSnapshot = hasSnapshot;
        await dbContext.SaveChangesAsync();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _jobQueue.Complete();
        await base.StopAsync(cancellationToken);

        try
        {
            await _guestStateRead.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await _guestStateCancellation.CancelAsync();
            _logger.LogWarning("Stopped before the guest-state read finished; it will be read again on next start.");
        }

        try
        {
            await _jobQueue.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stopped before in-flight Proxmox jobs finished; state will be reconciled on next start.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug($"Begin Processing Proxmox Virtual Machines");

                if (_proxmoxOptionsMonitor.CurrentValue.Enabled)
                {
                    var scope = _serviceProvider.CreateScope();
                    var handedOff = false;

                    try
                    {
                        handedOff = await ProcessVms(
                            scope,
                            scope.ServiceProvider.GetRequiredService<VmContext>(),
                            scope.ServiceProvider.GetRequiredService<IProxmoxService>(),
                            cancellationToken);
                    }
                    finally
                    {
                        // A pass that started a guest-state read gave it the scope, which it disposes.
                        if (!handedOff)
                            scope.Dispose();
                    }
                }
                else
                {
                    _logger.LogDebug("Proxmox disabled, skipping");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in {nameof(ProxmoxStateService)}");
            }

            var intervalSeconds = GetIntervalSeconds();
            _logger.LogDebug($"End Processing Proxmox Virtual Machines. Sleeping for {intervalSeconds} seconds.");

            await _resetEvent.WaitAsync(new TimeSpan(0, 0, intervalSeconds), cancellationToken);
        }
    }

    /// <summary>
    /// The configured poll interval, floored at one second. A configured 0 - the value an unset
    /// StateRefreshIntervalSeconds binds to - would make WaitAsync build a
    /// CancellationTokenSource(TimeSpan.Zero) that cancels immediately, turning this into a tight
    /// loop against the PVE API rather than a poll.
    /// </summary>
    private int GetIntervalSeconds()
    {
        var configured = _proxmoxOptionsMonitor.CurrentValue.StateRefreshIntervalSeconds;

        if (configured < MinimumIntervalSeconds)
        {
            if (_warnedIntervalSeconds != configured)
            {
                _warnedIntervalSeconds = configured;
                _logger.LogWarning(
                    $"Proxmox StateRefreshIntervalSeconds is {configured}, which would busy-loop. Using {MinimumIntervalSeconds} second(s) instead.");
            }

            return MinimumIntervalSeconds;
        }

        _warnedIntervalSeconds = null;
        return configured;
    }

    /// <summary>
    /// One power-state pass, saved before anything slower runs. Returns whether it handed its scope to a
    /// guest-state read, which then owns and disposes it.
    /// </summary>
    private async Task<bool> ProcessVms(
        IServiceScope scope, VmContext dbContext, IProxmoxService proxmoxService, CancellationToken cancellationToken)
    {
        var pveVms = (await proxmoxService.GetVms())
            .DistinctBy(x => x.VmId)
            .ToDictionary(x => x.VmId);

        var dbVms = await dbContext.Vms
            .Where(x => x.ProxmoxVmInfo != null)
            .ToListAsync(cancellationToken);

        _logger.LogDebug($"Found {pveVms.Count} {"machine".Pluralize(pveVms.Count)} in PVE and {dbVms.Count} {"machine".Pluralize(dbVms.Count)} in database.");

        foreach (var dbVm in dbVms)
        {
            pveVms.TryGetValue(dbVm.ProxmoxVmInfo.Id, out var pveVm);
            this.UpdateVm(dbVm, pveVm);
        }

        LogUpdated(await dbContext.SaveChangesAsync(cancellationToken));

        if (!_guestStateRead.IsCompleted || !ShouldReadGuestState())
            return false;

        _guestStateRead = ReadGuestStates(scope, dbContext, dbVms, pveVms, proxmoxService, _guestStateCancellation.Token);
        return true;
    }

    /// <summary>
    /// Reads and saves the guest-derived columns of the machines a pass loaded, on that pass's scope and
    /// change tracker, while later passes carry on with power state. Only HasSnapshot and IpAddresses are
    /// set here, and EF writes only the columns that changed, so this save cannot put back a power state
    /// or node a later pass has written since.
    /// </summary>
    private async Task ReadGuestStates(
        IServiceScope scope,
        VmContext dbContext,
        IReadOnlyList<Domain.Models.Vm> dbVms,
        IDictionary<long, IClusterResourceVm> pveVms,
        IProxmoxService proxmoxService,
        CancellationToken cancellationToken)
    {
        // Off the pass's own thread, so the pass returns as soon as it has handed over.
        await Task.Yield();

        try
        {
            var guestStates = await GetGuestStates(dbVms, pveVms, proxmoxService, cancellationToken);

            foreach (var dbVm in dbVms)
            {
                if (guestStates.TryGetValue(dbVm.Id, out var guestState))
                    UpdateGuestState(dbVm, guestState);
            }

            LogUpdated(await dbContext.SaveChangesAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception reading Proxmox guest state in {nameof(ProxmoxStateService)}");
        }
        finally
        {
            scope.Dispose();
        }
    }

    // Only an actual change is worth an Information entry. EF produces no Modified entry for a row whose
    // values did not change, so an idle cluster stays silent instead of logging every pass.
    private void LogUpdated(int count)
    {
        if (count > 0)
        {
            _logger.LogInformation($"Updated {count} {"machine".Pluralize(count)}");
        }
        else
        {
            _logger.LogDebug($"Updated {count} {"machine".Pluralize(count)}");
        }
    }

    /// <summary>
    /// Whether to start a guest-state read after this pass: on the first pass, then once
    /// GuestStateRefreshIntervalSeconds has passed since the last one started. Zero or less starts one
    /// after every pass whose predecessor has finished.
    /// </summary>
    private bool ShouldReadGuestState()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(0, _proxmoxOptionsMonitor.CurrentValue.GuestStateRefreshIntervalSeconds));

        if (_sinceGuestStateRead.IsRunning && _sinceGuestStateRead.Elapsed < interval)
            return false;

        _sinceGuestStateRead.Restart();
        return true;
    }

    /// <summary>
    /// Reads the per-Vm state of every tracked Vm the cluster reported, a bounded number at a time. Only
    /// tracked Vms are read - most of a real cluster is not in Player - and a Vm whose read fails is left
    /// out, so its stored values stand until a later pass succeeds.
    /// </summary>
    /// <remarks>
    /// The reads run off the change tracker on detached copies of <see cref="ProxmoxVmInfo"/>, addressed to
    /// the node /cluster/resources has just reported, so a migrated Vm is read where it now is and nothing
    /// here touches a tracked entity from another thread.
    /// </remarks>
    private async Task<IDictionary<Guid, ProxmoxGuestState>> GetGuestStates(
        IEnumerable<Domain.Models.Vm> dbVms,
        IDictionary<long, IClusterResourceVm> pveVms,
        IProxmoxService proxmoxService,
        CancellationToken cancellationToken)
    {
        var states = new ConcurrentDictionary<Guid, ProxmoxGuestState>();

        var targets = dbVms
            .Select(vm => (Vm: vm, Pve: pveVms.TryGetValue(vm.ProxmoxVmInfo.Id, out var pve) ? pve : null))
            .Where(x => x.Pve != null)
            .Select(x => (x.Vm.Id, Info: new ProxmoxVmInfo
            {
                VmId = x.Vm.Id,
                Id = x.Vm.ProxmoxVmInfo.Id,
                Node = x.Pve.Node,
                Type = x.Vm.ProxmoxVmInfo.Type,
            }, Running: x.Pve.IsRunning))
            .ToList();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _proxmoxOptionsMonitor.CurrentValue.GuestStateParallelism),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(targets, options, async (target, token) =>
        {
            try
            {
                var state = await proxmoxService.GetGuestState(target.Info, target.Running, token);

                if (state != null)
                    states[target.Id] = state;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One Vm's failure must not cost the rest of the pass their power state.
                _logger.LogWarning(ex, $"Could not read the guest state of Proxmox vmid={target.Info.Id}");
            }
        });

        return states;
    }

    private async Task ProcessVm(IClusterResourceVm pveVm)
    {
        if (pveVm == null) return;

        // An unhandled exception here faults the ActionBlock permanently, silently killing state
        // sync for the rest of the process lifetime, so nothing may escape this method.
        try
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<VmContext>();
                var dbVm = await dbContext.Vms.FirstOrDefaultAsync(x => x.ProxmoxVmInfo.Id == pveVm.VmId);

                if (dbVm != null)
                {
                    this.UpdateVm(dbVm, pveVm);
                    await dbContext.SaveChangesAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception processing Proxmox vmid={pveVm.VmId}");
        }
    }

    /// <remarks>
    /// Power state, type and node only. The guest-derived columns are left to
    /// <see cref="UpdateGuestState"/>: the single-Vm path does not read them, and a machine whose
    /// guest-state read failed keeps what it had.
    /// </remarks>
    private Domain.Models.Vm UpdateVm(Domain.Models.Vm vm, IClusterResourceVm pveVm)
    {
        if (pveVm != null)
        {
            vm.Type = Domain.Models.VmType.Proxmox;
            vm.PowerState = pveVm.GetPowerState();
            vm.ProxmoxVmInfo.Node = pveVm.Node;
        }

        return vm;
    }

    private static void UpdateGuestState(Domain.Models.Vm vm, ProxmoxGuestState guestState)
    {
        vm.HasSnapshot = guestState.HasSnapshot;

        // Only on a change, so an unchanged list is not a Modified entry and an idle cluster stays silent.
        if (guestState.IpAddresses != null &&
            !(vm.IpAddresses ?? []).SequenceEqual(guestState.IpAddresses))
        {
            vm.IpAddresses = guestState.IpAddresses;
        }
    }
}
