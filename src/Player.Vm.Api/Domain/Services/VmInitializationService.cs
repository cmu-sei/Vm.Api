// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Vsphere.Services;

namespace Player.Vm.Api.Domain.Services;

public sealed class VmInitializationService(
    VmInitializationQueue queue,
    IServiceProvider services,
    ILogger<VmInitializationService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunProvider(queue.Vsphere, VmType.Vsphere, stoppingToken),
        RunProvider(queue.Proxmox, VmType.Proxmox, stoppingToken));

    private async Task RunProvider(VmInitializationBatchQueue pending, VmType provider, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = await pending.ReadAsync(ct);
                var watch = Stopwatch.StartNew();
                Guid[] unresolved;
                try
                {
                    unresolved = await InitializeAsync(batch.Keys.ToArray(), provider, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "VM initialization failed for {Provider}", provider);
                    unresolved = batch.Keys.ToArray();
                }

                foreach (var id in unresolved)
                    pending.Retry(id, batch[id]);

                logger.LogInformation(
                    "VM initialization for {Provider}: {Count} queued, {Completed} completed or skipped, {Unresolved} unresolved in {ElapsedMs} ms",
                    provider, batch.Count, batch.Count - unresolved.Length, unresolved.Length, watch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task<Guid[]> InitializeAsync(Guid[] ids, VmType provider, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmContext>();
        var records = await db.Vms.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
        var eligible = records.Where(x => VmInitializationQueue.GetProvider(x) == provider).Select(x => x.Id).ToArray();
        if (eligible.Length == 0)
            return [];

        IReadOnlySet<Guid> resolved = provider == VmType.Proxmox
            ? await scope.ServiceProvider.GetRequiredService<IProxmoxStateService>().InitializeVmsAsync(eligible, ct)
            : await scope.ServiceProvider.GetRequiredService<IConnectionService>().InitializeVmsAsync(eligible, ct);
        return eligible.Except(resolved).ToArray();
    }
}
