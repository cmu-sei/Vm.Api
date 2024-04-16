// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Vsphere.Options;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using VimClient;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Memory;
using Player.Vm.Api.Domain.Models;
using Nito.AsyncEx;
using Player.Vm.Api.Infrastructure.Extensions;
using System.Collections.Concurrent;
using Player.Vm.Api.Domain.Vsphere.Models;

namespace Player.Vm.Api.Domain.Vsphere.Services
{
    public interface IMachineStateService
    {
        void CheckState();
    }

    public class MachineStateService : BackgroundService, IMachineStateService
    {
        private readonly ILogger<MachineStateService> _logger;
        private readonly IOptionsMonitor<VsphereOptions> _optionsMonitor;
        private readonly IServiceProvider _serviceProvider;
        private VmContext _dbContext;
        private IVsphereService _vsphereService;
        private readonly IConnectionService _connectionService;
        private AsyncAutoResetEvent _resetEvent = new AsyncAutoResetEvent(false);
        private ConcurrentDictionary<string, DateTime> _lastCheckedTimes = new ConcurrentDictionary<string, DateTime>();

        public MachineStateService(
                IOptionsMonitor<VsphereOptions> optionsMonitor,
                ILogger<MachineStateService> logger,
                IConnectionService connectionService,
                IServiceProvider serviceProvider
            )
        {
            _optionsMonitor = optionsMonitor;
            _logger = logger;
            _connectionService = connectionService;
            _serviceProvider = serviceProvider;
        }

        public void CheckState()
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
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        InitScope(scope);

                        var events = await GetEvents();
                        await ProcessEvents(events);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, $"Exception in {nameof(MachineStateService)}");
                }

                await _resetEvent.WaitAsync(
                    new TimeSpan(0, 0, 0, 0, _optionsMonitor.CurrentValue.CheckTaskProgressIntervalMilliseconds),
                    cancellationToken);
            }
        }

        private void InitScope(IServiceScope scope)
        {
            _dbContext = scope.ServiceProvider.GetRequiredService<VmContext>();
            _vsphereService = scope.ServiceProvider.GetRequiredService<IVsphereService>();
        }

        private async Task<Dictionary<string, IEnumerable<Event>>> GetEvents()
        {
            var connections = _connectionService.GetAllConnections();
            var taskList = new List<Task<KeyValuePair<string, IEnumerable<Event>>>>();
            var events = new Dictionary<string, IEnumerable<Event>>();

            foreach (var connection in connections)
            {
                if (connection.Enabled)
                {
                    taskList.Add(GetEvents(connection));
                }
            }

            var results = await Task.WhenAll(taskList);

            foreach (var kvp in results)
            {
                events.Add(kvp.Key, kvp.Value);
            }

            return events;
        }

        private async Task<KeyValuePair<string, IEnumerable<Event>>> GetEvents(VsphereConnection connection)
        {
            var lastCheckedTime = _lastCheckedTimes.GetOrAdd(connection.Address, DateTime.UtcNow);
            IEnumerable<Event> events;
            var now = DateTime.UtcNow;

            try
            {
                events = await _vsphereService.GetEvents(GetFilterSpec(lastCheckedTime), connection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception getting events from {connection.Address}");
                return new KeyValuePair<string, IEnumerable<Event>>(connection.Address, new List<Event>());
            }

            _lastCheckedTimes[connection.Address] = now;
            return new KeyValuePair<string, IEnumerable<Event>>(connection.Address, events);
        }

        private EventFilterSpec GetFilterSpec(DateTime beginTime)
        {
            var filterSpec = new EventFilterSpec()
            {
                time = new EventFilterSpecByTime()
                {
                    beginTime = beginTime,
                    beginTimeSpecified = true
                },
                eventTypeId = new string[]
                {
                    nameof(VmPoweredOnEvent),
                    nameof(DrsVmPoweredOnEvent),
                    nameof(VmPoweredOffEvent),
                }
            };

            return filterSpec;
        }

        private async Task ProcessEvents(Dictionary<string, IEnumerable<Event>> events)
        {
            var eventDict = new Dictionary<Guid, Event>();

            if (!events.SelectMany(x => x.Value).Any())
            {
                return;
            }

            foreach (var eventList in events)
            {
                var connectionEvents = eventList.Value;
                var filteredEvents = connectionEvents.GroupBy(x => x.vm.vm.Value)
                .Select(g => g.OrderByDescending(l => l.createdTime).First())
                .ToArray();

                foreach (var evt in filteredEvents)
                {
                    var id = _connectionService.GetVmIdByRef(evt.vm.vm.Value, eventList.Key);

                    if (id.HasValue)
                    {
                        eventDict.TryAdd(id.Value, evt);
                    }
                }
            }

            var vms = await _dbContext.Vms
                .Include(x => x.VmTeams)
                .Where(x => eventDict.Select(y => y.Key).Contains(x.Id))
                .ToListAsync();

            foreach (var vm in vms)
            {
                Event evt;
                if (eventDict.TryGetValue(vm.Id, out evt))
                {
                    var type = evt.GetType();

                    if (new Type[] { typeof(VmPoweredOnEvent), typeof(DrsVmPoweredOnEvent) }.Contains(type))
                    {
                        vm.PowerState = PowerState.On;
                    }
                    else if (type == typeof(VmPoweredOffEvent))
                    {
                        vm.PowerState = PowerState.Off;
                    }
                }
            }

            await _dbContext.SaveChangesAsync();
        }
    }
}