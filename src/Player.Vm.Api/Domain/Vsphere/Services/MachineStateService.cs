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

namespace Player.Vm.Api.Domain.Vsphere.Services
{
    public interface IMachineStateService
    {
        void CheckState();
    }

    public class MachineStateService : BackgroundService, IMachineStateService
    {
        private readonly ILogger<MachineStateService> _logger;
        private VsphereOptions _options;
        private readonly IOptionsMonitor<VsphereOptions> _optionsMonitor;
        private readonly IServiceProvider _serviceProvider;
        private VmContext _dbContext;
        private IVsphereService _vsphereService;
        private readonly IConnectionService _connectionService;
        private AsyncAutoResetEvent _resetEvent = new AsyncAutoResetEvent(false);
        private DateTime _lastCheckedTime = DateTime.UtcNow;

        public MachineStateService(
                IOptionsMonitor<VsphereOptions> optionsMonitor,
                ILogger<MachineStateService> logger,
                IMemoryCache cache,
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

                        if (_options.Enabled)
                        {
                            var events = await GetEvents();
                            await ProcessEvents(events);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, $"Exception in {nameof(MachineStateService)}");
                }

                await _resetEvent.WaitAsync(
                    new TimeSpan(0, 0, 0, 0, _options.CheckTaskProgressIntervalMilliseconds),
                    cancellationToken);
            }
        }

        private void InitScope(IServiceScope scope)
        {
            _dbContext = scope.ServiceProvider.GetRequiredService<VmContext>();
            _vsphereService = scope.ServiceProvider.GetRequiredService<IVsphereService>();
            _options = _optionsMonitor.CurrentValue;
        }

        private async Task<IEnumerable<Event>> GetEvents()
        {
            var now = DateTime.UtcNow;
            var events = await _vsphereService.GetEvents(GetFilterSpec(_lastCheckedTime));
            _lastCheckedTime = now;
            return events;
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

        private async Task ProcessEvents(IEnumerable<Event> events)
        {
            var eventDict = new Dictionary<Guid, Event>();

            if (!events.Any())
            {
                return;
            }

            var filteredEvents = events.GroupBy(x => x.vm.vm.Value)
                .Select(g => g.OrderByDescending(l => l.createdTime).First())
                .ToArray();

            foreach (var evt in filteredEvents)
            {
                var id = _connectionService.GetVmIdByRef(evt.vm.vm.Value);

                if (id.HasValue)
                {
                    eventDict.TryAdd(id.Value, evt);
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