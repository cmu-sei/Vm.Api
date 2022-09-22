// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
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
    private VmContext _dbContext;
    private IProxmoxService _proxmoxService;
    private IOptionsMonitor<ProxmoxOptions> _proxmoxOptionsMonitor;
    private ProxmoxOptions _proxmoxOptions;

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
        _resetEvent.Set();
    }

    public async Task UpdateVm(IClusterResourceVm vm)
    {
        await _jobQueue.SendAsync(vm);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _jobQueue.Complete();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation($"Begin Processing Proxmox Virtual Machines");

                using (var scope = _serviceProvider.CreateScope())
                {
                    InitScope(scope);

                    if (_proxmoxOptionsMonitor.CurrentValue.Enabled)
                    {
                        await ProcessVms();
                    }
                    else
                    {
                        _logger.LogInformation("Proxmox disabled, skipping");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in {nameof(ProxmoxStateService)}");
            }

            _logger.LogInformation($"End Processing Proxmox Virtual Machines. Sleeping for {_proxmoxOptions.StateRefreshIntervalSeconds} seconds.");

            await _resetEvent.WaitAsync(
                new TimeSpan(0, 0, _proxmoxOptions.StateRefreshIntervalSeconds),
                cancellationToken);
        }
    }

    private void InitScope(IServiceScope scope)
    {
        _dbContext = scope.ServiceProvider.GetRequiredService<VmContext>();
        _proxmoxService = scope.ServiceProvider.GetRequiredService<IProxmoxService>();
        _proxmoxOptions = _proxmoxOptionsMonitor.CurrentValue;
    }

    private async Task ProcessVms()
    {
        var pveVms = await _proxmoxService.GetVms();
        var dbVms = await _dbContext.Vms
            .Where(x => x.ProxmoxVmInfo != null)
            .ToListAsync();

        _logger.LogInformation($"Found {pveVms.Count()} {"machine".Pluralize(pveVms.Count())} in PVE and {dbVms.Count} {"machine".Pluralize(dbVms.Count)} in database.");

        foreach (var dbVm in dbVms)
        {
            var pveVm = pveVms.FirstOrDefault(x => x.VmId == dbVm.ProxmoxVmInfo.Id);
            this.UpdateVm(dbVm, pveVm);
        }

        var count = await _dbContext.SaveChangesAsync();
        _logger.LogInformation($"Updated {count} {"machine".Pluralize(count)}");
    }

    private async Task ProcessVm(IClusterResourceVm pveVm)
    {
        if (pveVm == null) return;

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
}