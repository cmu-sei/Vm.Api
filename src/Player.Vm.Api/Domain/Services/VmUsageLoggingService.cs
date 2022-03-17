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
using System.Threading.Tasks;
using Player.Vm.Api.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Player.Vm.Api.Domain.Services
{
    public interface IVmUsageLoggingService
    {
        Task CreateVmLogEntry(Guid userId, Guid vmId, IEnumerable<Guid> teamIds, CancellationToken ct);
        Task CloseVmLogEntry(Guid userId, Guid vmId, CancellationToken ct);
    }

    public class VmUsageLoggingService : IVmUsageLoggingService
    {
         private readonly IServiceScopeFactory _scopeFactory;
        private readonly VmUsageLoggingOptions _loggingOptions;
        private readonly IPlayerService _playerService;
        private readonly IVmService _vmService;

        public VmUsageLoggingService(
            IServiceScopeFactory scopeFactory, 
            VmUsageLoggingOptions loggingOptions,
            IPlayerService playerService,
            IVmService vmService)
        {
            _scopeFactory = scopeFactory;
            _loggingOptions = loggingOptions;
            _playerService = playerService;
            _vmService = vmService;
        }

       public async Task CreateVmLogEntry(Guid userId, Guid vmId, IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            if (_loggingOptions.Enabled == false)
            {
                return;
            }

            Player.Vm.Api.Features.Vms.Vm vm = null;
            User user = null;
            var teams = teamIds.ToArray<Guid>();

            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<VmLoggingContext>();
                var activeSessions = await dbContext.VmUsageLoggingSessions
                    .Where(s => s.SessionEnd <= DateTimeOffset.MinValue &&
                        s.SessionStart <= DateTimeOffset.UtcNow)
                    .ToArrayAsync();

                foreach (var session in activeSessions)
                {
                    // First check to see if there is a matching TeamID in either list and only then proceed
                    var teamFound = false;
                    foreach (var teamId in session.TeamIds)
                    {
                        if (teams.Contains(teamId))
                        {
                            teamFound = true;
                            break;
                        }
                    }

                    if (!teamFound)
                    {
                        // This session doesn't have the team associated so skip it
                        continue;
                    }

                    if (user == null)
                    {
                        // Get the User info once
                        user = await _playerService.GetUserById(userId, ct);

                        // Get the Vm info once
                        vm = await _vmService.GetAsync(vmId, ct);
                    }

                    var logEntry = new VmUsageLogEntry {
                        Session = session,
                        VmId = vm.Id,
                        VmName = vm.Name,
                        IpAddress = string.Join(", ", vm.IpAddresses),
                        UserId = user.Id,
                        UserName = user.Name,
                        VmActiveDT = DateTimeOffset.UtcNow,
                        VmInactiveDT = DateTimeOffset.MinValue
                    };

                    await dbContext.VmUsageLogEntries.AddAsync(logEntry);
                    await dbContext.SaveChangesAsync();
                }
            }

        

        }

        public async Task CloseVmLogEntry(Guid userId, Guid vmId, CancellationToken ct)
        {
            if (_loggingOptions.Enabled == false)
            {
                return;
            }

            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<VmLoggingContext>();
                var vmUsageLogEntries =  await dbContext.VmUsageLogEntries
                    .Where(e => e.UserId == userId && 
                        e.VmId == vmId && 
                        e.VmInactiveDT <= DateTimeOffset.MinValue)
                    .ToArrayAsync();

                if (vmUsageLogEntries == null)
                {
                    // Unable to find a matching log entry
                    return;
                }

                foreach (var log in vmUsageLogEntries)
                {
                    log.VmInactiveDT = DateTimeOffset.UtcNow;
                }
                
                await dbContext.SaveChangesAsync();
            }
        }
    }

}
