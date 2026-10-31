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
    /// Set by <see cref="CheckState"/>: a caller that asks for a pass has just changed something - a
    /// revert, a power operation - and should see its effect on the guest-derived columns too.
    /// </summary>
    private int _guestStateRequested = 1;

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

    public void CheckState()
    {
        Interlocked.Exchange(ref _guestStateRequested, 1);
        _resetEvent.Set();
    }

    public async Task UpdateVm(IClusterResourceVm vm)
    {
        await _jobQueue.SendAsync(vm);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _jobQueue.Complete();
        await base.StopAsync(cancellationToken);

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
                    using var scope = _serviceProvider.CreateScope();
                    await ProcessVms(
                        scope.ServiceProvider.GetRequiredService<VmContext>(),
                        scope.ServiceProvider.GetRequiredService<IProxmoxService>(),
                        cancellationToken);
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

    private async Task ProcessVms(VmContext dbContext, IProxmoxService proxmoxService, CancellationToken cancellationToken)
    {
        var pveVms = (await proxmoxService.GetVms())
            .DistinctBy(x => x.VmId)
            .ToDictionary(x => x.VmId);

        var dbVms = await dbContext.Vms
            .Where(x => x.ProxmoxVmInfo != null)
            .ToListAsync(cancellationToken);

        _logger.LogDebug($"Found {pveVms.Count} {"machine".Pluralize(pveVms.Count)} in PVE and {dbVms.Count} {"machine".Pluralize(dbVms.Count)} in database.");

        var guestStates = ShouldReadGuestState()
            ? await GetGuestStates(dbVms, pveVms, proxmoxService, cancellationToken)
            : new Dictionary<Guid, ProxmoxGuestState>();

        foreach (var dbVm in dbVms)
        {
            pveVms.TryGetValue(dbVm.ProxmoxVmInfo.Id, out var pveVm);
            guestStates.TryGetValue(dbVm.Id, out var guestState);
            this.UpdateVm(dbVm, pveVm, guestState);
        }

        var count = await dbContext.SaveChangesAsync(cancellationToken);

        // Only an actual change is worth an Information entry. EF produces no Modified entry for a row
        // whose values did not change, so an idle cluster stays silent instead of logging every pass.
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
    /// Whether this pass also reads guest state: when asked to through <see cref="CheckState"/>, or once
    /// GuestStateRefreshIntervalSeconds has passed since the last read. Zero or less reads it every pass.
    /// </summary>
    private bool ShouldReadGuestState()
    {
        var requested = Interlocked.Exchange(ref _guestStateRequested, 0) == 1;
        var interval = TimeSpan.FromSeconds(Math.Max(0, _proxmoxOptionsMonitor.CurrentValue.GuestStateRefreshIntervalSeconds));

        if (!requested && _sinceGuestStateRead.IsRunning && _sinceGuestStateRead.Elapsed < interval)
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
    /// A null <paramref name="guestState"/> leaves the guest-derived columns as they are: the single-Vm
    /// path does not read them, and a pass whose read failed has nothing to say about them.
    /// </remarks>
    private Domain.Models.Vm UpdateVm(
        Domain.Models.Vm vm,
        IClusterResourceVm pveVm,
        ProxmoxGuestState guestState = null)
    {
        if (pveVm != null)
        {
            vm.Type = Domain.Models.VmType.Proxmox;
            vm.PowerState = pveVm.GetPowerState();
            vm.ProxmoxVmInfo.Node = pveVm.Node;

            if (guestState != null)
            {
                vm.HasSnapshot = guestState.HasSnapshot;
            }
        }

        return vm;
    }
}
