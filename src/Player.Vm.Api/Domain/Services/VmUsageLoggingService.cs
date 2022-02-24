// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Data;
using Player.Vm.Api.Features.Vms;
using Player.Api.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Player.Vm.Api.Domain.Services
{
    public interface IVmUsageLoggingService
    {
        void CreateVmLogEntry(Guid userId, Guid vmId, IEnumerable<Guid> teamIds);
        void CloseVmLogEntry(Guid vmLogEntryId, Guid vmId);
    }

    public class VmUsageLoggingService : IVmUsageLoggingService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public VmUsageLoggingService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async void CreateVmLogEntry(Guid userId, Guid vmId, IEnumerable<Guid> teamIds)
        {
            var ct = new CancellationToken();
            Player.Vm.Api.Features.Vms.Vm vm = null;
            User user = null;

            foreach (Guid teamId in teamIds)
            {
                using (var scope = _scopeFactory.CreateScope())
                {                
                    var dbContext = scope.ServiceProvider.GetRequiredService<VmLoggingContext>();
                    var activeSessions = await dbContext.VmUsageLoggingSessions
                        .Where(s => s.TeamId == teamId && 
                            s.SessionEnd <= DateTimeOffset.MinValue &&
                            s.SessionStart <= DateTimeOffset.UtcNow)
                        .ToArrayAsync();

                    foreach (var session in activeSessions)
                    {
                        if (user == null)
                        {
                            // Get the User info once
                            using (var playerScope = _scopeFactory.CreateScope())
                            {
                                var playerApi = scope.ServiceProvider.GetRequiredService<IPlayerService>();
                                user = await playerApi.GetUserById(userId, ct);
                            }

                            // Get the Vm info once
                            using (var vmScope = _scopeFactory.CreateScope())
                            {
                                var vmApi = scope.ServiceProvider.GetRequiredService<IVmService>();
                                vm = await vmApi.GetAsync(vmId, ct);
                            }
                        }
                        var logEntry = new VmUsageLogEntry {
                            SessionId = session.Id,
                            VmId = vm.Id,
                            VmName = vm.Name,
                            UserId = user.Id,
                            UserName = user.Name,
                            VmActiveDT = DateTimeOffset.UtcNow,
                            VmInActiveDT = DateTimeOffset.MinValue
                        };

                        await dbContext.VmUsageLogEntries.AddAsync(logEntry);
                        await dbContext.SaveChangesAsync();
                    }
                }
            }

        }

        public async void CloseVmLogEntry(Guid userId, Guid vmId)
        {
            
            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<VmLoggingContext>();
                var vmUsageLogEntries =  await dbContext.VmUsageLogEntries
                    .Where(e => e.UserId == userId && 
                        e.VmId == vmId && 
                        e.VmInActiveDT <= DateTimeOffset.MinValue)
                    .ToArrayAsync();

                if (vmUsageLogEntries == null)
                {
                    // Unable to find a matching log entry
                    return;
                }

                foreach (var log in vmUsageLogEntries)
                {
                    log.VmInActiveDT = DateTimeOffset.UtcNow;
                    dbContext.Update<VmUsageLogEntry>(log);    
                }
                
                await dbContext.SaveChangesAsync();
            }            
        }
    }

}
