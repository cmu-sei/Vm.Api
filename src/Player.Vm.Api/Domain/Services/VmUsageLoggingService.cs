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

namespace Player.Vm.Api.Domain.Services;

public interface IVmUsageLoggingService
{
    Task CreateVmLogEntry(Guid userId, Guid vmId, IEnumerable<Guid> teamIds, CancellationToken ct);
    Task CloseVmLogEntry(Guid userId, Guid vmId, CancellationToken ct);
}

public class DisabledVmUsageLoggingService : IVmUsageLoggingService
{
    public async Task CreateVmLogEntry(Guid userId, Guid vmId, IEnumerable<Guid> teamIds, CancellationToken ct)
    {
        await Task.CompletedTask;
    }

    public async Task CloseVmLogEntry(Guid userId, Guid vmId, CancellationToken ct)
    {
        await Task.CompletedTask;
    }
}

public class VmUsageLoggingService : IVmUsageLoggingService
{
    private readonly VmUsageLoggingOptions _loggingOptions;
    private readonly IPlayerService _playerService;
    private readonly IVmService _vmService;
    private readonly VmLoggingContext _dbContext;

    public VmUsageLoggingService(
        VmUsageLoggingOptions loggingOptions,
        IPlayerService playerService,
        IVmService vmService,
        VmLoggingContext dbContext)
    {
        _loggingOptions = loggingOptions;
        _playerService = playerService;
        _vmService = vmService;
        _dbContext = dbContext;
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

        var activeSessions = await _dbContext.VmUsageLoggingSessions
            .Where(s => (s.SessionEnd <= DateTimeOffset.MinValue || s.SessionEnd > DateTimeOffset.UtcNow) &&
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

            var logEntry = new VmUsageLogEntry
            {
                Session = session,
                VmId = vm.Id,
                VmName = vm.Name,
                IpAddress = string.Join(", ", vm.IpAddresses),
                UserId = user.Id,
                UserName = user.Name,
                VmActiveDT = DateTimeOffset.UtcNow,
                VmInactiveDT = DateTimeOffset.MinValue
            };

            await _dbContext.VmUsageLogEntries.AddAsync(logEntry);
            await _dbContext.SaveChangesAsync();
        }
    }

    public async Task CloseVmLogEntry(Guid userId, Guid vmId, CancellationToken ct)
    {
        if (_loggingOptions.Enabled == false)
        {
            return;
        }

        var vmUsageLogEntries = await _dbContext.VmUsageLogEntries
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

        await _dbContext.SaveChangesAsync();
    }
}
