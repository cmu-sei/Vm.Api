// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using VimClient;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Extensions;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Features.Vms.Hubs;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using Nito.AsyncEx;
using Player.Vm.Api.Infrastructure.Extensions;
using Player.Vm.Api.Domain.Services.HealthChecks;

namespace Player.Vm.Api.Domain.Vsphere.Services
{
    public interface ITaskService
    {
        void CheckTasks();
    }

    public class TaskService : BackgroundService, ITaskService
    {
        private readonly IHubContext<ProgressHub> _progressHub;
        private readonly ILogger<TaskService> _logger;
        private readonly IOptionsMonitor<VsphereOptions> _optionsMonitor;
        private readonly IServiceProvider _serviceProvider;
        private VmContext _dbContext;

        private IConnectionService _connectionService;
        private IMachineStateService _machineStateService;
        private ConcurrentDictionary<string, List<Notification>> _runningTasks = new ConcurrentDictionary<string, List<Notification>>();
        private AsyncAutoResetEvent _resetEvent = new AsyncAutoResetEvent(false);
        private bool _tasksPending = false;
        private readonly TaskServiceHealthCheck _taskServiceHealthCheck;


        public TaskService(
                IOptionsMonitor<VsphereOptions> optionsMonitor,
                ILogger<TaskService> logger,
                IHubContext<ProgressHub> progressHub,
                IConnectionService connectionService,
                IMachineStateService machineStateService,
                IServiceProvider serviceProvider,
                TaskServiceHealthCheck taskServiceHealthCheck
            )
        {
            _optionsMonitor = optionsMonitor;
            _logger = logger;
            _progressHub = progressHub;
            _connectionService = connectionService;
            _serviceProvider = serviceProvider;
            _machineStateService = machineStateService;
            _taskServiceHealthCheck = taskServiceHealthCheck;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _tasksPending = false;

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        _dbContext = scope.ServiceProvider.GetRequiredService<VmContext>();
                        await processTasks();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception in TaskService");
                }

                var intervalMilliseconds = _tasksPending ?
                    _optionsMonitor.CurrentValue.ReCheckTaskProgressIntervalMilliseconds :
                    _optionsMonitor.CurrentValue.CheckTaskProgressIntervalMilliseconds;

                _taskServiceHealthCheck.HealthAllowance = _optionsMonitor.CurrentValue.HealthAllowanceSeconds;
                _taskServiceHealthCheck.CompletedRun();
                await _resetEvent.WaitAsync(new TimeSpan(0, 0, 0, 0, intervalMilliseconds));
            }
        }

        public void CheckTasks()
        {
            _resetEvent.Set();
        }

        private async Task processTasks()
        {
            await getRecentTasks();
            foreach (var vmTasks in _runningTasks)
            {
                try
                {
                    await _progressHub.Clients.Group(vmTasks.Key).SendAsync("Progress", vmTasks.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception in processTasks");
                }
            }
        }

        private async Task getRecentTasks()
        {
            var pendingVms = await _dbContext.Vms
                .Include(x => x.VmTeams)
                .Where(x => x.HasPendingTasks)
                .ToArrayAsync();

            var stillPendingVmIds = new List<Guid>();

            var responseDict = new Dictionary<VsphereConnection, Task<RetrievePropertiesResponse>>();
            var connections = _connectionService.GetAllConnections();

            foreach (var connection in connections)
            {
                if (connection.Enabled && connection.Sic != null && connection.Props != null)
                {
                    PropertyFilterSpec[] filters = createPFSForRecentTasks(connection.Sic.taskManager);
                    var task = connection.Client.RetrievePropertiesAsync(connection.Props, filters);
                    responseDict.Add(connection, task);
                }
            }

            try
            {
                await Task.WhenAll(responseDict.Select(x => x.Value));
            }
            catch (Exception)
            {
                // Will check for exceptions in individual tasks below
            }

            _runningTasks.Clear();
            var forceCheckMachineState = false;

            foreach (var kvp in responseDict)
            {
                if (kvp.Value.Status != TaskStatus.RanToCompletion)
                {
                    _logger.LogError(kvp.Value.Exception, $"Exception retrieving tasks from {kvp.Key.Address}");
                    continue;
                }

                foreach (var task in kvp.Value.Result.returnval)
                {
                    try
                    {
                        Guid? vmId = null;
                        var vmRef = task.GetProperty("info.entity") != null ? ((ManagedObjectReference)task.GetProperty("info.entity")).Value : null;

                        if (vmRef != null)
                        {
                            vmId = _connectionService.GetVmIdByRef(vmRef, kvp.Key.Address);
                        }

                        var broadcastTime = DateTime.UtcNow.ToString();
                        var taskId = task.GetProperty("info.key") != null ? task.GetProperty("info.key").ToString() : "";
                        var taskType = task.GetProperty("info.descriptionId") != null ? task.GetProperty("info.descriptionId").ToString() : "";
                        var progress = task.GetProperty("info.progress") != null ? task.GetProperty("info.progress").ToString() : "";
                        var state = task.GetProperty("info.state") != null ? task.GetProperty("info.state").ToString() : "";
                        var notification = new Notification()
                        {
                            broadcastTime = DateTime.UtcNow.ToString(),
                            taskId = taskId,
                            taskName = taskType.Replace("VirtualMachine.", ""),
                            taskType = taskType,
                            progress = progress,
                            state = state
                        };

                        if (vmId.HasValue)
                        {
                            var id = vmId.Value.ToString();
                            var vmTasks = _runningTasks.ContainsKey(id) ? _runningTasks[id] : new List<Notification>();
                            vmTasks.Add(notification);
                            _runningTasks.AddOrUpdate(id, vmTasks, (k, v) => (v = vmTasks));
                        }


                        if (state == TaskInfoState.queued.ToString() || state == TaskInfoState.running.ToString())
                        {
                            _tasksPending = true;

                            if (vmId.HasValue)
                            {
                                stillPendingVmIds.Add(vmId.Value);
                            }
                        }

                        if (state == TaskInfoState.success.ToString() &&
                            this.GetPowerTaskTypes().Contains(taskType))
                        {
                            if (vmId.HasValue)
                            {
                                forceCheckMachineState = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Exception processing task from {kvp.Key.Address}");
                    }
                }
            }

            foreach (var vm in pendingVms)
            {
                if (!stillPendingVmIds.Contains(vm.Id))
                {
                    vm.HasPendingTasks = false;
                }
            }

            var vmsToUpdate = await _dbContext.Vms
                .Include(x => x.VmTeams)
                .Where(x => stillPendingVmIds.Contains(x.Id))
                .ToArrayAsync();

            foreach (var vm in vmsToUpdate)
            {
                vm.HasPendingTasks = true;
            }

            await _dbContext.SaveChangesAsync();

            if (forceCheckMachineState)
            {
                _machineStateService.CheckState();
            }
        }

        private string[] GetPowerTaskTypes()
        {
            return new string[]
            {
                "VirtualMachine.powerOff",
                "VirtualMachine.powerOn",
            };
        }


        private PropertyFilterSpec[] createPFSForRecentTasks(ManagedObjectReference taskManagerRef)
        {
            PropertySpec pSpec = new PropertySpec
            {
                all = false,
                type = "Task",
                pathSet = new string[]
                {
                    "info.entity",
                    "info.descriptionId",
                    "info.name",
                    "info.state",
                    "info.progress",
                    "info.cancelled",
                    "info.error",
                    "info.key"
                }
            };

            ObjectSpec oSpec = new ObjectSpec
            {
                obj = taskManagerRef,
                skip = false,
                skipSpecified = true
            };

            TraversalSpec tSpec = new TraversalSpec
            {
                type = "TaskManager",
                path = "recentTask",
                skip = false
            };


            oSpec.selectSet = new SelectionSpec[] { tSpec };

            PropertyFilterSpec pfSpec = new PropertyFilterSpec
            {
                propSet = new PropertySpec[] { pSpec },
                objectSet = new ObjectSpec[] { oSpec }
            };

            return new PropertyFilterSpec[] { pfSpec };
        }
    }
}