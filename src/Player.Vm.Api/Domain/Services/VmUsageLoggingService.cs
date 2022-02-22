// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Data;
using Player.Vm.Api.Features.Vms;
using Player.Api.Client;

namespace Player.Vm.Api.Domain.Services
{
    public interface IVmUsageLoggingService
    {
        void CreateVmLogEntry(Guid userId, Guid vmId, IEnumerable<Guid> teamIds);
        void CloseVmLogEntry(Guid vmLogEntryId);
    }

    public class VmUsageLoggingService : IVmUsageLoggingService
    {

        private readonly ConcurrentDictionary<Guid, VmUsageLogEntry> _activeLogEntries = new ConcurrentDictionary<Guid, VmUsageLogEntry>();
        private readonly VmLoggingContext _db;
        private readonly IPlayerService _playerService;
        private readonly IVmService _vmService;

        public VmUsageLoggingService(
            VmLoggingContext db, 
            IPlayerService playerService,
            IVmService vmService)
        {
            _db = db;
            _playerService = playerService;
            _vmService = vmService;
           
        }

        public async void CreateVmLogEntry(Guid userId, Guid vmId, IEnumerable<Guid> teamIds)
        {
            Console.WriteLine("Creating Log Entry:  ");
            var ct = new CancellationToken();

            Player.Vm.Api.Features.Vms.Vm vm = null;
            User user = null;

            foreach (Guid teamId in teamIds)
            {
                var activeSession = _db.VmUsageLoggingSessions.FirstOrDefault(s => s.TeamId == teamId && s.SessionEnd > DateTimeOffset.MinValue);
                if (activeSession != null)
                {
                    if (user == null)
                    {
                        // Get the User info once
                        user = await _playerService.GetUserById(userId, ct);
                        // Get the Vm info once
                        vm = await _vmService.GetAsync(vmId, ct);
                    }
                    /*var logEntry = new VmUsageLogEntry {
                        SessionId = activeSession.Id;

                    };*/
                }
            }

        }

        public void CloseVmLogEntry(Guid vmLogEntryId)
        {
            Console.WriteLine("Closing Log Entry:  ");
            
        }
    }

}
