// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using VimClient;
using AutoMapper;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Extensions;
using Player.Vm.Api.Domain.Models;
using System.Web;

namespace Player.Vm.Api.Domain.Vsphere.Services
{
    public interface IVsphereService
    {
        Task<VsphereVirtualMachine> GetMachineById(Guid id);
        Task<string> GetConsoleUrl(VsphereVirtualMachine machine);
        Task<NicOptions> GetNicOptions(Guid id, bool canManage, IEnumerable<string> allowedNetworks, VsphereVirtualMachine machine);
        Task<string> PowerOnVm(Guid id);
        Task<string> PowerOffVm(Guid id);
        Task<string> RebootVm(Guid id);
        Task<string> ShutdownVm(Guid id);
        Task<TaskInfo> ReconfigureVm(Guid id, Feature feature, string label, string newvalue);
        Task<VirtualMachineToolsStatus> GetVmToolsStatus(Guid id);
        Task<string> UploadFileToVm(Guid id, string username, string password, string filepath, Stream fileStream);
        Task<string> GetVmFileUrl(Guid id, string username, string password, string filepath);
        Task<IEnumerable<IsoFile>> GetIsos(Guid vmId, string viewId, string subFolder);
        Task<string> SetResolution(Guid id, int width, int height);
        Task<ManagedObjectReference[]> BulkPowerOperation(Guid[] ids, PowerOperation operation);
        Task<Dictionary<Guid, string>> BulkShutdown(Guid[] ids);
        Task<Dictionary<Guid, string>> BulkReboot(Guid[] ids);
        Task<Dictionary<Guid, PowerState>> GetPowerState(IEnumerable<Guid> machineIds);
        Task<IEnumerable<Event>> GetEvents(EventFilterSpec filterSpec, VsphereConnection connection);
        Task RevertToCurrentSnapshot(Guid vmId);
    }

    public class VsphereService : IVsphereService
    {
        private RewriteHostOptions _rewriteHostOptions;

        private readonly ILogger<VsphereService> _logger;
        int _pollInterval = 1000;

        private readonly IConfiguration _configuration;
        private readonly IConnectionService _connectionService;
        private readonly IMapper _mapper;

        public VsphereService(
                IOptions<RewriteHostOptions> rewriteHostOptions,
                ILogger<VsphereService> logger,
                IConfiguration configuration,
                IConnectionService connectionService,
                IMapper mapper
            )
        {
            _rewriteHostOptions = rewriteHostOptions.Value;
            _logger = logger;
            _connectionService = connectionService;
            _configuration = configuration;
            _mapper = mapper;
        }

        public async Task<string> GetConsoleUrl(VsphereVirtualMachine machine)
        {
            if (machine.State == "off")
            {
                return null;
            }

            return await GetConsoleUrl(machine.Id);
        }

        public async Task<string> GetConsoleUrl(Guid id)
        {
            var aggregate = await this.GetVm(id);

            VirtualMachineTicket ticket = null;
            string url = null;

            try
            {
                ticket = await aggregate.Connection.Client.AcquireTicketAsync(aggregate.MachineReference, "webmks");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get ticket for vm {id}");
                return url;
            }

            string host = string.Empty;

            // ticket.host is null when using esxi instead of vcenter
            if (ticket.host != null)
                host = ticket.host;
            else
                host = aggregate.Connection.Address;

            if (_rewriteHostOptions.RewriteHost)
            {
                url = $"wss://{_rewriteHostOptions.RewriteHostUrl}/ticket/{ticket.ticket}?{_rewriteHostOptions.RewriteHostQueryParam}={host}";
            }
            else
            {
                url = $"wss://{host}/ticket/{ticket.ticket}";
            }

            _logger.LogDebug($"Returning url: {url}");

            return url;
        }

        public async Task<VsphereAggregate> GetVm(Guid id)
        {
            VsphereAggregate aggregate = _connectionService.GetAggregate(id);

            // Vm not found, check all connections for it
            if (aggregate == null)
            {
                List<Task<VsphereAggregate>> taskList = new List<Task<VsphereAggregate>>();

                foreach (var connection in _connectionService.GetAllConnections())
                {
                    taskList.Add(this.FindVm(id, connection));
                }

                var results = await Task.WhenAll(taskList);
                aggregate = results.Where(x => x != null).FirstOrDefault();
            }

            return aggregate;
        }

        private async Task<VsphereAggregate> FindVm(Guid id, VsphereConnection connection)
        {
            VsphereAggregate aggregate = null;

            try
            {
                var vmReference = await connection.Client.FindByUuidAsync(connection.Sic.searchIndex, null, id.ToString(), true, false);

                if (vmReference != null)
                {
                    aggregate = new VsphereAggregate(connection, vmReference);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"Did not find machine with id: {id} on connection to {connection.Address}");
            }

            return aggregate;
        }

        public async Task<string> PowerOnVm(Guid id)
        {
            _logger.LogDebug($"Power on vm {id} requested");

            ManagedObjectReference vmReference = null;
            ManagedObjectReference task;
            string state = null;

            var aggregate = await this.GetVm(id);
            vmReference = aggregate.MachineReference;

            if (vmReference == null)
            {
                _logger.LogDebug($"Could not get vm reference");
                return state;
            }
            state = await GetPowerState(id);

            if (state == "on")
            {
                state = "already running";
                _logger.LogDebug($"Returning state: {state}");
                return state;
            }

            try
            {
                task = await aggregate.Connection.Client.PowerOnVM_TaskAsync(vmReference, null);

                // TaskInfo info = await WaitForVimTask(task);
                // if (info.state == TaskInfoState.success) {
                //     state = "started";
                // }
                // else
                // {
                //     throw new Exception(info.error.localizedMessage);
                // }

                state = "poweron submitted";
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Failed to send power on " + id);
                state = "poweron error";
            }

            state = "poweron submitted";

            _logger.LogDebug($"Returning state: {state}");

            return state;
        }

        public async Task<string> PowerOffVm(Guid id)
        {
            ManagedObjectReference vmReference = null;
            ManagedObjectReference task;
            string state = null;

            var aggregate = await this.GetVm(id);
            vmReference = aggregate.MachineReference;

            if (vmReference == null)
            {
                _logger.LogDebug($"Could not get vm reference");
                return state;
            }

            state = await GetPowerState(id);

            _logger.LogDebug($"Power off vm {id} requested");

            if (state == "off")
            {
                state = "already off";
                _logger.LogDebug($"Returning state: {state}");
                return state;
            }

            try
            {
                task = await aggregate.Connection.Client.PowerOffVM_TaskAsync(vmReference);

                //TaskInfo info = await WaitForVimTask(task);
                // if (info.state == TaskInfoState.success) {
                //     State = VmPowerState.off;
                //     state = "stopped";
                // }
                // else
                // {
                //     throw new Exception(info.error.localizedMessage);
                // }
                state = "poweroff submitted";
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Failed to send power off " + id);
                state = "poweroff error";
            }

            _logger.LogDebug($"Returning state: {state}");

            return state;
        }

        public async Task<ManagedObjectReference[]> BulkPowerOperation(Guid[] ids, PowerOperation operation)
        {
            List<Task<ManagedObjectReference>> taskList = new List<Task<ManagedObjectReference>>();

            foreach (var id in ids)
            {
                var aggregate = await this.GetVm(id);
                var vmReference = aggregate.MachineReference;

                if (vmReference == null)
                {
                    _logger.LogDebug($"Error getting vmReference for {id}");
                    continue;
                }

                try
                {
                    Task<ManagedObjectReference> taskReference = null;

                    switch (operation)
                    {
                        case PowerOperation.PowerOff:
                            taskReference = aggregate.Connection.Client.PowerOffVM_TaskAsync(vmReference);
                            break;
                        case PowerOperation.PowerOn:
                            taskReference = aggregate.Connection.Client.PowerOnVM_TaskAsync(vmReference, null);
                            break;
                        case PowerOperation.Revert:
                            taskReference = aggregate.Connection.Client.RevertToCurrentSnapshot_TaskAsync(vmReference, null, false);
                            break;
                    }

                    taskList.Add(taskReference);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to create power task for {id}");
                }
            }

            await Task.WhenAll(taskList);

            return taskList.Select(x => x.Result).ToArray();
        }

        public async Task<string> RebootVm(Guid id)
        {
            // need to wait vm to poweroff before telling it to poweron

            ManagedObjectReference vmReference = null;
            ManagedObjectReference task;

            var aggregate = await this.GetVm(id);
            vmReference = aggregate.MachineReference;

            if (vmReference == null)
            {
                _logger.LogDebug($"Could not get vm reference");
                return "error";
            }

            if (await GetPowerState(id) == "error")
            {
                return "error";
            }

            try
            {
                task = await aggregate.Connection.Client.PowerOffVM_TaskAsync(vmReference);

                TaskInfo info = await WaitForVimTask(task, aggregate.Connection);
                if (info.state != TaskInfoState.success)
                {
                    return "error";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Failed to send power off " + id);
                return "error";
            }

            return await PowerOnVm(id);
        }

        public async Task<string> ShutdownVm(Guid id)
        {
            ManagedObjectReference vmReference = null;
            string state = null;

            var aggregate = await this.GetVm(id);
            vmReference = aggregate.MachineReference;

            if (vmReference == null)
            {
                _logger.LogDebug($"Could not get vm reference");
                return "error";
            }

            state = await GetPowerState(id);

            _logger.LogDebug($"Shutdown OS for vm {id} requested");

            if (state == "off")
            {
                state = "already off";
                _logger.LogDebug($"Returning state: {state}");
                return state;
            }

            try
            {
                await aggregate.Connection.Client.ShutdownGuestAsync(vmReference);
                state = "shutdown submitted";
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Failed to send shutdown " + id);
                return "error";
            }

            return state;
        }

        public async Task<Dictionary<Guid, string>> BulkShutdown(Guid[] ids)
        {
            Dictionary<Guid, string> retDict = new Dictionary<Guid, string>();
            Dictionary<Guid, Task> taskDict = new Dictionary<Guid, Task>();

            foreach (var id in ids)
            {
                var aggregate = await this.GetVm(id);
                var vmReference = aggregate.MachineReference;

                if (vmReference == null)
                {
                    _logger.LogDebug($"Could not get vm reference for {id}");
                    retDict.Add(id, "Virtual machine not found");
                    continue;
                }

                taskDict.Add(id, aggregate.Connection.Client.ShutdownGuestAsync(vmReference));
            }

            try
            {
                await Task.WhenAll(taskDict.Values.Where(x => x != null)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Expected exception if shutdown failed, handled in finally
            }
            finally
            {
                foreach (var kvp in taskDict)
                {
                    if (kvp.Value.Exception == null)
                    {
                        retDict.Add(kvp.Key, string.Empty);
                    }
                    else
                    {
                        retDict.Add(kvp.Key, kvp.Value.Exception.InnerException.Message);
                    }
                }
            }

            return retDict;
        }

        public async Task<Dictionary<Guid, string>> BulkReboot(Guid[] ids)
        {
            Dictionary<Guid, string> retDict = new Dictionary<Guid, string>();
            Dictionary<Guid, Task> taskDict = new Dictionary<Guid, Task>();

            foreach (var id in ids)
            {
                var aggregate = await this.GetVm(id);
                var vmReference = aggregate.MachineReference;

                if (vmReference == null)
                {
                    _logger.LogDebug($"Could not get vm reference for {id}");
                    retDict.Add(id, "Virtual machine not found");
                    continue;
                }

                taskDict.Add(id, aggregate.Connection.Client.RebootGuestAsync(vmReference));
            }

            try
            {
                await Task.WhenAll(taskDict.Values.Where(x => x != null)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Expected exception if reboot failed, handled in finally
            }
            finally
            {
                foreach (var kvp in taskDict)
                {
                    if (kvp.Value.Exception == null)
                    {
                        retDict.Add(kvp.Key, string.Empty);
                    }
                    else
                    {
                        retDict.Add(kvp.Key, kvp.Value.Exception.InnerException.Message);
                    }
                }
            }

            return retDict;
        }

        public string GetPowerState(RetrievePropertiesResponse propertiesResponse)
        {
            VmPowerState State = VmPowerState.off;
            string state = null;

            VimClient.ObjectContent[] oc = propertiesResponse.returnval;
            VimClient.ObjectContent obj = oc[0];

            foreach (DynamicProperty dp in obj.propSet)
            {
                if (dp.val.GetType() == typeof(VirtualMachineSummary))
                {
                    try
                    {
                        VirtualMachineSummary summary = (VirtualMachineSummary)dp.val;
                        State = (summary.runtime.powerState == VirtualMachinePowerState.poweredOn)
                            ? VmPowerState.running
                            : VmPowerState.off;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex.Message);
                    }
                }
                else if (dp.val.GetType() == typeof(VirtualMachinePowerState))
                {
                    switch (((VirtualMachinePowerState)dp.val))
                    {
                        case VirtualMachinePowerState.poweredOff:
                            State = VmPowerState.off;
                            break;
                        case VirtualMachinePowerState.poweredOn:
                            State = VmPowerState.running;
                            break;
                        case VirtualMachinePowerState.suspended:
                            State = VmPowerState.suspended;
                            break;
                        default:
                            continue;
                    }
                }
            }

            if (State == VmPowerState.running)
            {
                state = "on";
            }
            else if (State == VmPowerState.off)
            {
                state = "off";
            }
            else
            {
                state = "error";
            }
            return state;
        }

        public async Task<string> GetPowerState(Guid id)
        {
            _logger.LogDebug("GetPowerState called");

            var aggregate = await this.GetVm(id);
            var vmReference = aggregate.MachineReference;

            if (vmReference == null)
            {
                _logger.LogDebug($"could not get state, vmReference is null");
                return "error";
            }

            //retrieve the properties specified
            RetrievePropertiesResponse response = await aggregate.Connection.Client.RetrievePropertiesAsync(
                aggregate.Connection.Props,
                VmFilter(vmReference, "summary.runtime.powerState"));

            return GetPowerState(response);
        }

        public async Task<Dictionary<Guid, PowerState>> GetPowerState(IEnumerable<Guid> machineIds)
        {
            var vmRefDict = new Dictionary<string, List<ManagedObjectReference>>();

            foreach (var id in machineIds)
            {
                var aggregate = await this.GetVm(id);
                var vmReference = aggregate.MachineReference;

                if (vmReference != null)
                {
                    if (!vmRefDict.ContainsKey(aggregate.Connection.Address))
                    {
                        vmRefDict.Add(aggregate.Connection.Address, new List<ManagedObjectReference>());
                    }

                    vmRefDict[aggregate.Connection.Address].Add(vmReference);
                }
            }

            var connections = _connectionService.GetAllConnections();
            List<Dictionary<Guid, PowerState>> powerStates = new List<Dictionary<Guid, PowerState>>();

            foreach (var kvp in vmRefDict)
            {
                var connection = connections.Where(x => x.Address == kvp.Key).FirstOrDefault();

                // retrieve the properties specified
                RetrievePropertiesResponse response = await connection.Client.RetrievePropertiesAsync(
                    connection.Props,
                    VmFilter(kvp.Value, "summary.runtime.powerState config.uuid"));

                powerStates.Add(GetPowerStateMultiple(response));
            }

            return powerStates.SelectMany(x => x).ToDictionary(x => x.Key, y => y.Value);
        }

        private Dictionary<Guid, PowerState> GetPowerStateMultiple(RetrievePropertiesResponse propertiesResponse)
        {
            var dict = new Dictionary<Guid, PowerState>();

            foreach (var obj in propertiesResponse.returnval)
            {
                var idProp = obj.propSet.Where(x => x.name == "config.uuid").FirstOrDefault();
                var powerStateProp = obj.propSet.Where(x => x.name == "summary.runtime.powerState").FirstOrDefault();

                Guid id;

                if (idProp != null && Guid.TryParse(idProp.val.ToString(), out id))
                {
                    if (powerStateProp != null && powerStateProp.val is VirtualMachinePowerState)
                    {
                        PowerState powerState;

                        switch (((VirtualMachinePowerState)powerStateProp.val))
                        {
                            case VirtualMachinePowerState.poweredOff:
                                powerState = PowerState.Off;
                                break;
                            case VirtualMachinePowerState.poweredOn:
                                powerState = PowerState.On;
                                break;
                            case VirtualMachinePowerState.suspended:
                                powerState = PowerState.Suspended;
                                break;
                            default:
                                continue;
                        }

                        dict.Add(id, powerState);
                    }
                }
            }

            return dict;
        }

        public VirtualMachineToolsStatus GetVmToolsStatus(RetrievePropertiesResponse propertiesResponse)
        {
            VimClient.ObjectContent[] oc = propertiesResponse.returnval;
            VimClient.ObjectContent obj = oc[0];
            foreach (DynamicProperty dp in obj.propSet)
            {
                if (dp.val.GetType() == typeof(VirtualMachineSummary))
                {
                    VirtualMachineSummary vmSummary = (VirtualMachineSummary)dp.val;
                    //check vmware tools status
                    var toolsStatus = vmSummary.guest.toolsStatus;
                    return toolsStatus;
                }
            }
            return VirtualMachineToolsStatus.toolsNotRunning;
        }

        public async Task<VirtualMachineToolsStatus> GetVmToolsStatus(Guid id)
        {
            var aggregate = await GetVm(id);
            var vmReference = aggregate.MachineReference;

            //retrieve the properties specificied
            RetrievePropertiesResponse response = await aggregate.Connection.Client.RetrievePropertiesAsync(
                aggregate.Connection.Props,
                VmFilter(vmReference));

            return GetVmToolsStatus(response);
        }

        public async Task<string> UploadFileToVm(Guid id, string username, string password, string filepath, Stream fileStream)
        {
            _logger.LogDebug("UploadFileToVm called");

            var aggregate = await GetVm(id);
            var vmReference = aggregate.MachineReference;

            if (vmReference == null)
            {
                var errorMessage = $"could not upload file, vmReference is null";
                _logger.LogDebug(errorMessage);
                return errorMessage;
            }
            //retrieve the properties specificied
            RetrievePropertiesResponse response = await aggregate.Connection.Client.RetrievePropertiesAsync(
                aggregate.Connection.Props,
                VmFilter(vmReference));

            VimClient.ObjectContent[] oc = response.returnval;
            VimClient.ObjectContent obj = oc[0];

            foreach (DynamicProperty dp in obj.propSet)
            {
                if (dp.val.GetType() == typeof(VirtualMachineSummary))
                {
                    VirtualMachineSummary vmSummary = (VirtualMachineSummary)dp.val;
                    //check vmware tools status
                    var tools_status = vmSummary.guest.toolsStatus;
                    if (tools_status == VirtualMachineToolsStatus.toolsNotInstalled || tools_status == VirtualMachineToolsStatus.toolsNotRunning)
                    {
                        var errorMessage = $"could not upload file, VM Tools is not running";
                        _logger.LogDebug(errorMessage);
                        return errorMessage;
                    }

                    // user credentials on the VM
                    NamePasswordAuthentication credentialsAuth = new NamePasswordAuthentication()
                    {
                        interactiveSession = false,
                        username = username,
                        password = password
                    };
                    ManagedObjectReference fileManager = new ManagedObjectReference()
                    {
                        type = "GuestFileManager",
                        Value = "guestOperationsFileManager"
                    };
                    // upload the file
                    GuestFileAttributes fileAttributes = new GuestFileAttributes();
                    var fileTransferUrl = aggregate.Connection.Client.InitiateFileTransferToGuestAsync(fileManager, vmReference, credentialsAuth, filepath, fileAttributes, fileStream.Length, true).Result;

                    // Replace IP address with hostname
                    RetrievePropertiesResponse hostResponse = await aggregate.Connection.Client.RetrievePropertiesAsync(aggregate.Connection.Props, HostFilter(vmSummary.runtime.host, "name"));
                    string hostName = hostResponse.returnval[0].propSet[0].val as string;

                    if (!fileTransferUrl.Contains(hostName))
                    {
                        fileTransferUrl = fileTransferUrl.Replace("https://", "");
                        var s = fileTransferUrl.IndexOf("/");
                        fileTransferUrl = "https://" + hostName + fileTransferUrl.Substring(s);
                    }

                    // http put to url
                    using (var httpClientHandler = new HttpClientHandler())
                    {
                        using (var httpClient = new HttpClient(httpClientHandler))
                        {
                            httpClient.DefaultRequestHeaders.Accept.Clear();
                            using (MemoryStream ms = new MemoryStream())
                            {
                                var timeout = _configuration.GetSection("vmOptions").GetValue("Timeout", 3);
                                httpClient.Timeout = TimeSpan.FromMinutes(timeout);
                                fileStream.CopyTo(ms);
                                var fileContent = new ByteArrayContent(ms.ToArray());
                                _logger.LogDebug("UploadFileToVm Upload URL:  " + fileTransferUrl);
                                var uploadResponse = await httpClient.PutAsync(fileTransferUrl, fileContent);
                            }
                        }
                    }
                }
            }
            return "";
        }

        public async Task<string> GetVmFileUrl(Guid id, string username, string password, string filepath)
        {
            var aggregate = await GetVm(id);
            var vmReference = aggregate.MachineReference;

            if (vmReference == null)
            {
                var errorMessage = $"could not get file url, vmReference is null";
                _logger.LogDebug(errorMessage);
                return errorMessage;
            }

            //retrieve the properties specificied
            RetrievePropertiesResponse response = await aggregate.Connection.Client.RetrievePropertiesAsync(
                aggregate.Connection.Props,
                VmFilter(vmReference));

            VimClient.ObjectContent[] oc = response.returnval;
            VimClient.ObjectContent obj = oc[0];

            foreach (DynamicProperty dp in obj.propSet)
            {
                if (dp.val.GetType() == typeof(VirtualMachineSummary))
                {
                    VirtualMachineSummary vmSummary = (VirtualMachineSummary)dp.val;
                    //check vmware tools status
                    var tools_status = vmSummary.guest.toolsStatus;
                    if (tools_status == VirtualMachineToolsStatus.toolsNotInstalled || tools_status == VirtualMachineToolsStatus.toolsNotRunning)
                    {
                        var errorMessage = $"could not get file url, VM Tools is not running";
                        _logger.LogDebug(errorMessage);
                        return errorMessage;
                    }

                    // user credentials on the VM
                    NamePasswordAuthentication credentialsAuth = new NamePasswordAuthentication()
                    {
                        interactiveSession = false,
                        username = username,
                        password = password
                    };
                    ManagedObjectReference fileManager = new ManagedObjectReference()
                    {
                        type = "GuestFileManager",
                        Value = "guestOperationsFileManager"
                    };

                    var fileTransferInfo = await aggregate.Connection.Client.InitiateFileTransferFromGuestAsync(fileManager, vmReference, credentialsAuth, filepath);
                    var fileTransferUrl = fileTransferInfo.url;

                    // Replace IP address with hostname
                    RetrievePropertiesResponse hostResponse = await aggregate.Connection.Client.RetrievePropertiesAsync(aggregate.Connection.Props, HostFilter(vmSummary.runtime.host, "name"));
                    string hostName = hostResponse.returnval[0].propSet[0].val as string;

                    if (!fileTransferUrl.Contains(hostName))
                    {
                        fileTransferUrl = fileTransferUrl.Replace("https://", "");
                        var s = fileTransferUrl.IndexOf("/");
                        fileTransferUrl = "https://" + hostName + fileTransferUrl.Substring(s);
                    }

                    if (_rewriteHostOptions.RewriteHost)
                    {
                        var builder = new UriBuilder(fileTransferUrl)
                        {
                            Port = -1
                        };

                        var query = HttpUtility.ParseQueryString(builder.Query);
                        query[_rewriteHostOptions.RewriteHostQueryParam] = builder.Host;
                        var fileName = this.GetFileName(filepath);
                        query["fileName"] = fileName;
                        builder.Query = query.ToString();

                        builder.Host = _rewriteHostOptions.RewriteHostUrl;
                        fileTransferUrl = builder.ToString();
                    }

                    return fileTransferUrl;
                }
            }
            return "";
        }

        private string GetFileName(string filePath)
        {
            var fileUri = new Uri($"file://{filePath}");
            return Path.GetFileName(fileUri.ToString());
        }

        private async Task<TaskInfo> WaitForVimTask(ManagedObjectReference task, VsphereConnection connection)
        {
            int i = 0;
            TaskInfo info = new TaskInfo();

            //iterate the search until complete or timeout occurs
            do
            {
                //check every so often
                await Task.Delay(_pollInterval);
                info = await GetVimTaskInfo(task, connection);
                i++;
                //check for status updates until the task is complete
            } while ((info.state == TaskInfoState.running || info.state == TaskInfoState.queued));

            //return the task info
            return info;
        }

        private async Task<TaskInfo> GetVimTaskInfo(ManagedObjectReference task, VsphereConnection connection)
        {
            TaskInfo info = new TaskInfo();
            RetrievePropertiesResponse response = await connection.Client.RetrievePropertiesAsync(
                connection.Props,
                TaskFilter(task));
            VimClient.ObjectContent[] oc = response.returnval;
            info = (TaskInfo)oc[0].propSet[0].val;
            return info;
        }

        public async Task<NicOptions> GetNicOptions(Guid id, bool canManage, IEnumerable<string> allowedNetworks, VsphereVirtualMachine machine)
        {
            return new NicOptions
            {
                AvailableNetworks = await GetVmNetworks(machine, canManage, allowedNetworks),
                CurrentNetworks = await GetVMConfiguration(machine, Feature.net)
            };
        }

        public async Task<List<string>> GetVmNetworks(VsphereVirtualMachine machine, bool canManage, IEnumerable<string> allowedNetworks)
        {
            var aggregate = await this.GetVm(machine.Id);
            List<Network> hostNetworks = _connectionService.GetNetworksByHost(machine.HostReference, aggregate.Connection.Address);
            List<string> networkNames = hostNetworks.Select(n => n.Name).ToList();

            // if a user can manage this VM, then they have access to all available NICs
            if (canManage)
            {
                return networkNames.OrderBy(x => x).ToList();
            }
            else
            {
                if (allowedNetworks != null)
                {
                    return networkNames.Intersect(allowedNetworks, StringComparer.InvariantCultureIgnoreCase).OrderBy(x => x).ToList();
                }
                else
                {
                    return new List<string>();
                }
            }
        }

        public async Task<Dictionary<string, string>> GetVMConfiguration(VsphereVirtualMachine machine, Feature feature)
        {
            var aggregate = await this.GetVm(machine.Id);
            VirtualDevice[] devices = machine.Devices;

            VirtualMachineConfigSpec vmcs = new VirtualMachineConfigSpec();
            Dictionary<string, string> names = new Dictionary<string, string>();
            switch (feature)
            {
                case Feature.iso:
                    IEnumerable<Description> cdroms = devices.OfType<VirtualCdrom>().Select(c => c.deviceInfo);
                    foreach (Description d in cdroms)
                    {
                        if (d != null)
                        {
                            names.Add(d.label, d.summary);
                        }
                    }
                    break;

                case Feature.net:
                case Feature.eth:
                    IEnumerable<VirtualEthernetCard> cards = devices.OfType<VirtualEthernetCard>();
                    foreach (VirtualEthernetCard c in cards)
                    {
                        var backingInfo = c.backing;
                        var deviceInfo = c.deviceInfo;
                        if (backingInfo != null && deviceInfo != null)
                        {
                            if (backingInfo.GetType() == typeof(VirtualEthernetCardDistributedVirtualPortBackingInfo))
                            {
                                var card = backingInfo as VirtualEthernetCardDistributedVirtualPortBackingInfo; var portGroupKey = card?.port?.portgroupKey;

                                if (!string.IsNullOrEmpty(portGroupKey))
                                {
                                    var network = _connectionService.GetNetworkByReference(portGroupKey, aggregate.Connection.Address);
                                    string cardName = network?.Name;

                                    if (!string.IsNullOrEmpty(cardName))
                                    {
                                        names.Add(deviceInfo.label, cardName);
                                    }
                                }
                            }
                            else if (backingInfo.GetType() == typeof(VirtualEthernetCardNetworkBackingInfo))
                            {
                                var card = backingInfo as VirtualEthernetCardNetworkBackingInfo;
                                names.Add(deviceInfo.label, card.deviceName);
                            }
                            //
                        }
                    }
                    break;
                default:
                    throw new Exception("Invalid request.");
                    //break;
            }
            return names;
        }

        public async Task<IEnumerable<IsoFile>> GetIsos(Guid vmId, string viewId, string subfolder)
        {
            var aggregate = await this.GetVm(vmId);
            var connection = aggregate.Connection;

            List<IsoFile> list = new List<IsoFile>();
            var dsName = connection.Host.DsName;
            var baseFolder = connection.Host.BaseFolder;
            var filepath = $"[{dsName}] {baseFolder}/{viewId}/{subfolder}";

            var datastore = await GetDatastoreByName(dsName, connection);
            if (datastore == null)
            {
                _logger.LogError($"Datastore {dsName} not found in {connection.Address}.");
                return list;
            }

            var dsBrowser = datastore.Browser;

            ManagedObjectReference task = null;
            TaskInfo info = null;
            HostDatastoreBrowserSearchSpec spec = new HostDatastoreBrowserSearchSpec { };
            List<HostDatastoreBrowserSearchResults> results = new List<HostDatastoreBrowserSearchResults>();
            task = await connection.Client.SearchDatastore_TaskAsync(dsBrowser, filepath, spec);
            info = await WaitForVimTask(task, connection);
            if (info.state == TaskInfoState.error)
            {
                if (info.error.fault != null &&
                    info.error.fault.ToString().Equals("FileNotFound", StringComparison.CurrentCultureIgnoreCase))
                {
                    // folder not found, return empty
                    return list;
                }
                _logger.LogError(info.error.localizedMessage);
            }
            else if (info.result != null)
            {
                results.Add((HostDatastoreBrowserSearchResults)info.result);
            }

            foreach (HostDatastoreBrowserSearchResults result in results)
            {
                if (result != null && result.file != null && result.file.Length > 0)
                {
                    foreach (var fileInfo in result.file.Where(x => x.path.EndsWith(".iso")))
                    {
                        list.Add(new IsoFile(result.folderPath, fileInfo.path));
                    }
                }
            }

            return list;
        }

        public async Task<TaskInfo> ReconfigureVm(Guid id, Feature feature, string label, string newvalue)
        {
            var aggregate = await this.GetVm(id);
            VsphereVirtualMachine machine = await GetMachineById(id);
            ManagedObjectReference vmReference = machine.Reference;

            VirtualDevice[] devices = machine.Devices;
            VirtualMachineConfigSpec vmcs = new VirtualMachineConfigSpec();

            switch (feature)
            {
                case Feature.iso:
                    VirtualCdrom cdrom = (VirtualCdrom)((!string.IsNullOrEmpty(label))
                        ? devices.Where(o => o.deviceInfo.label == label).SingleOrDefault()
                        : devices.OfType<VirtualCdrom>().FirstOrDefault());

                    if (cdrom != null)
                    {
                        if (cdrom.backing.GetType() != typeof(VirtualCdromIsoBackingInfo))
                            cdrom.backing = new VirtualCdromIsoBackingInfo();

                        ((VirtualCdromIsoBackingInfo)cdrom.backing).datastore = (await GetDatastoreByName(aggregate.Connection.Host.DsName, aggregate.Connection)).Reference;
                        ((VirtualCdromIsoBackingInfo)cdrom.backing).fileName = newvalue;
                        cdrom.connectable = new VirtualDeviceConnectInfo
                        {
                            connected = true,
                            startConnected = true
                        };

                        vmcs.deviceChange = new VirtualDeviceConfigSpec[] {
                            new VirtualDeviceConfigSpec {
                                device = cdrom,
                                operation = VirtualDeviceConfigSpecOperation.edit,
                                operationSpecified = true
                            }
                        };
                    }
                    break;

                case Feature.net:
                case Feature.eth:
                    VirtualEthernetCard card = (VirtualEthernetCard)((!string.IsNullOrEmpty(label))
                        ? devices.Where(o => o.deviceInfo.label == label).SingleOrDefault()
                        : devices.OfType<VirtualEthernetCard>().FirstOrDefault());

                    if (card != null)
                    {
                        Network network = _connectionService.GetNetworkByName(newvalue, aggregate.Connection.Address);

                        if (network.IsDistributed)
                        {
                            card.backing = new VirtualEthernetCardDistributedVirtualPortBackingInfo
                            {
                                port = new DistributedVirtualSwitchPortConnection
                                {
                                    portgroupKey = network.Reference,
                                    switchUuid = network.SwitchId
                                }
                            };
                        }
                        else
                        {
                            card.backing = new VirtualEthernetCardNetworkBackingInfo
                            {
                                deviceName = newvalue
                            };
                        }

                        //if (card.backing is VirtualEthernetCardNetworkBackingInfo)
                        //    ((VirtualEthernetCardNetworkBackingInfo)card.backing).deviceName = newvalue;

                        //if (card.backing is VirtualEthernetCardDistributedVirtualPortBackingInfo)
                        //    ((VirtualEthernetCardDistributedVirtualPortBackingInfo)card.backing).port.portgroupKey = newvalue;

                        card.connectable = new VirtualDeviceConnectInfo()
                        {
                            connected = true,
                            startConnected = true,
                        };

                        vmcs.deviceChange = new VirtualDeviceConfigSpec[] {
                            new VirtualDeviceConfigSpec {
                                device = card,
                                operation = VirtualDeviceConfigSpecOperation.edit,
                                operationSpecified = true
                            }
                        };
                    }
                    break;

                case Feature.boot:
                    int delay = 0;
                    if (Int32.TryParse(newvalue, out delay))
                        vmcs.AddBootOption(delay);
                    break;

                //case Feature.guest:
                //    if (newvalue.HasValue() && !newvalue.EndsWith("\n"))
                //        newvalue += "\n";
                //    vmcs.annotation = config.annotation + newvalue;
                //    if (vm.State == VmPowerState.running && vmcs.annotation.HasValue())
                //        vmcs.AddGuestInfo(Regex.Split(vmcs.annotation, "\r\n|\r|\n"));
                //    break;

                default:
                    throw new Exception("Invalid change request.");
                    //break;
            }

            ManagedObjectReference task = await aggregate.Connection.Client.ReconfigVM_TaskAsync(vmReference, vmcs);
            TaskInfo info = await WaitForVimTask(task, aggregate.Connection);
            if (info.state == TaskInfoState.error)
                throw new Exception(info.error.localizedMessage);
            return info;
        }

        public async Task<string> SetResolution(Guid id, int width, int height)
        {
            var aggregate = await this.GetVm(id);
            var vmReference = aggregate.MachineReference;
            string state = await GetPowerState(id);

            if (vmReference == null)
            {
                _logger.LogDebug($"Could not get vm reference");
                return "error";
            }

            _logger.LogDebug($"Set Resolution vm {id} requested - {width}x{height}");

            if (state == "off")
            {
                state = "vm is powered off";
                _logger.LogDebug($"Returning state: {state}");
                return state;
            }

            try
            {
                await aggregate.Connection.Client.SetScreenResolutionAsync(vmReference, width, height);
                state = "set resolution submitted";
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Failed to set resolution for vm " + id);
                return "error";
            }

            return state;
        }

        public async Task<IEnumerable<Event>> GetEvents(EventFilterSpec filterSpec, VsphereConnection connection)
        {
            var events = new List<Event>();
            const int maxCount = 1000; // maximum allowable by vsphere api

            if (connection.Client != null)
            {
                var collector = await connection.Client.CreateCollectorForEventsAsync(connection.Sic.eventManager, filterSpec);
                int resultCount;

                do
                {
                    var response = await connection.Client.ReadNextEventsAsync(collector, maxCount);
                    events.AddRange(response.returnval);
                    resultCount = response.returnval.Length;
                }
                while (resultCount != 0);
                await connection.Client.DestroyCollectorAsync(collector);
            }

            return events;
        }

        public async Task RevertToCurrentSnapshot(Guid vmId)
        {
            var aggregate = await this.GetVm(vmId);
            var task = await aggregate.Connection.Client.RevertToCurrentSnapshot_TaskAsync(aggregate.MachineReference, null, false);
            var taskInfo = await WaitForVimTask(task, aggregate.Connection);

            if (taskInfo.state == TaskInfoState.error)
                throw new Exception(taskInfo.error.localizedMessage);
        }

        #region Filters

        public static PropertyFilterSpec[] TaskFilter(ManagedObjectReference mor)
        {
            PropertySpec prop = new PropertySpec
            {
                type = "Task",
                pathSet = new string[] { "info" }
            };

            ObjectSpec objectspec = new ObjectSpec
            {
                obj = mor,
            };

            return new PropertyFilterSpec[] {
                new PropertyFilterSpec {
                    propSet = new PropertySpec[] { prop },
                    objectSet = new ObjectSpec[] { objectspec }
                }
            };
        }

        public static PropertyFilterSpec[] VmFilter(ManagedObjectReference mor)
        {
            return VmFilter(new ManagedObjectReference[] { mor }, "summary");
        }

        public static PropertyFilterSpec[] VmFilter(IEnumerable<ManagedObjectReference> managedObjectReferences)
        {
            return VmFilter(managedObjectReferences, "summary");
        }

        public static PropertyFilterSpec[] VmFilter(ManagedObjectReference mor, string props)
        {
            return VmFilter(new ManagedObjectReference[] { mor }, props);
        }

        public static PropertyFilterSpec[] VmFilter(IEnumerable<ManagedObjectReference> managedObjectReferences, string props)
        {
            PropertySpec prop = new PropertySpec
            {
                type = "VirtualMachine",
                pathSet = props.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            };

            var objectSpecList = new List<ObjectSpec>();

            foreach (var mor in managedObjectReferences)
            {
                ObjectSpec objectspec = new ObjectSpec
                {
                    obj = mor, //_vms or vm-mor
                    selectSet = new SelectionSpec[] {
                        new TraversalSpec {
                            type = "Folder",
                            path = "childEntity"
                        }
                    }
                };

                objectSpecList.Add(objectspec);
            }

            PropertyFilterSpec[] ret = new PropertyFilterSpec[] {
                new PropertyFilterSpec {
                    propSet = new PropertySpec[] { prop },
                    objectSet = objectSpecList.ToArray()
                }
            };

            return ret;
        }

        public static PropertyFilterSpec[] HostFilter(ManagedObjectReference mor, string props)
        {
            PropertySpec prop = new PropertySpec
            {
                type = "HostSystem",
                pathSet = props.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            };

            ObjectSpec objectspec = new ObjectSpec
            {
                obj = mor, //_vms or vm-mor
            };

            PropertyFilterSpec[] ret = new PropertyFilterSpec[] {
                new PropertyFilterSpec {
                    propSet = new PropertySpec[] { prop },
                    objectSet = new ObjectSpec[] { objectspec }
                }
            };

            return ret;
        }

        public static PropertyFilterSpec[] SwitchFilter(ManagedObjectReference mor)
        {
            PropertySpec prop = new PropertySpec
            {
                type = "VmwareDistributedVirtualSwitch",
                pathSet = new string[] { "config", "uuid", }
            };

            ObjectSpec objectspec = new ObjectSpec
            {
                obj = mor, //_vms or vm-mor
            };

            PropertyFilterSpec[] ret = new PropertyFilterSpec[] {
                new PropertyFilterSpec {
                    propSet = new PropertySpec[] { prop },
                    objectSet = new ObjectSpec[] { objectspec }
                }
            };

            return ret;
        }

        public static PropertyFilterSpec[] PortGroupFilter(ManagedObjectReference mor)
        {
            PropertySpec prop = new PropertySpec
            {
                type = "DistributedVirtualPortgroup",
                pathSet = new string[] { "config" }
            };

            ObjectSpec objectspec = new ObjectSpec
            {
                obj = mor, //_vms or vm-mor
            };

            PropertyFilterSpec[] ret = new PropertyFilterSpec[] {
                new PropertyFilterSpec {
                    propSet = new PropertySpec[] { prop },
                    objectSet = new ObjectSpec[] { objectspec }
                }
            };

            return ret;
        }

        public static PropertyFilterSpec[] NetworkSummaryFilter(ManagedObjectReference mor, string props)
        {
            PropertySpec prop = new PropertySpec
            {
                type = "Network",
                pathSet = props.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            };

            ObjectSpec objectspec = new ObjectSpec
            {
                obj = mor, //_vms or vm-mor
            };

            PropertyFilterSpec[] ret = new PropertyFilterSpec[] {
                new PropertyFilterSpec {
                    propSet = new PropertySpec[] { prop },
                    objectSet = new ObjectSpec[] { objectspec }
                }
            };

            return ret;
        }

        public static PropertyFilterSpec[] DatastoreFilter(ManagedObjectReference mor)
        {
            PropertySpec prop = new PropertySpec
            {
                type = "Datastore",
                pathSet = new string[] { "browser", "summary" }
            };

            ObjectSpec objectspec = new ObjectSpec
            {
                obj = mor, //_res
                selectSet = new SelectionSpec[] {
                    new TraversalSpec {
                        type = "ComputeResource",
                        path = "datastore"
                    }
                }
            };

            return new PropertyFilterSpec[] {
                new PropertyFilterSpec {
                    propSet = new PropertySpec[] { prop },
                    objectSet = new ObjectSpec[] { objectspec }
                }
            };
        }


        #endregion

        #region Getters

        public async Task<VsphereVirtualMachine> GetMachineById(Guid id)
        {
            var aggregate = await this.GetVm(id);
            var machineReference = aggregate.MachineReference;

            // retrieve all machine properties we need
            RetrievePropertiesResponse propertiesResponse = await aggregate.Connection.Client.RetrievePropertiesAsync(
                aggregate.Connection.Props,
                VmFilter(machineReference, "name summary.guest.toolsStatus summary.runtime.host summary.runtime.powerState config.hardware.device snapshot"));

            VimClient.ObjectContent vm = propertiesResponse.returnval.FirstOrDefault();

            var snapshots = vm.GetProperty("snapshot") as VirtualMachineSnapshotInfo;

            var toolsStatus = vm.GetProperty("summary.guest.toolsStatus") as Nullable<VirtualMachineToolsStatus>;
            VirtualMachineToolsStatus vmToolsStatus = VirtualMachineToolsStatus.toolsNotRunning;
            if (toolsStatus != null)
            {
                vmToolsStatus = toolsStatus.Value;
            }

            VsphereVirtualMachine machine = new VsphereVirtualMachine
            {
                Devices = vm.GetProperty("config.hardware.device") as VirtualDevice[],
                HostReference = ((ManagedObjectReference)vm.GetProperty("summary.runtime.host")).Value,
                Id = id,
                Name = vm.GetProperty("name") as string,
                Reference = vm.obj,
                State = (VirtualMachinePowerState)vm.GetProperty("summary.runtime.powerState") == VirtualMachinePowerState.poweredOn ? "on" : "off",
                VmToolsStatus = vmToolsStatus,
                HasSnapshot = snapshots == null ? false : snapshots.rootSnapshotList.Any()
            };

            return machine;
        }

        private async Task<Datastore> GetDatastoreByName(string dsName, VsphereConnection connection)
        {
            Datastore datastore = _connectionService.GetDatastoreByName(dsName, connection.Address);

            if (datastore == null)
            {
                try
                {
                    // lookup reference
                    datastore = await GetNewDatastore(dsName, connection);
                }
                catch (Exception ex)
                {
                    datastore = null;
                    _logger.LogError(ex, $"Datastore {dsName} not found in {connection.Address}");
                }

                // return null if not found
                if (datastore == null)
                {
                    return null;
                }
            }

            if (connection.Client == null)
            {
                return null;
            }

            return datastore;
        }

        private async Task<Datastore> GetNewDatastore(string dsName, VsphereConnection connection)
        {
            var clunkyTree = await LoadReferenceTree(connection);
            if (clunkyTree.Length == 0)
            {
                throw new InvalidOperationException();
            }
            var datastores = clunkyTree.FindType("Datastore");
            foreach (VimClient.ObjectContent rawDatastore in datastores)
            {
                if (dsName == rawDatastore.GetProperty("name").ToString())
                {
                    return new Datastore()
                    {
                        Name = rawDatastore.GetProperty("name").ToString(),
                        Reference = rawDatastore.obj,
                        Browser = (ManagedObjectReference)rawDatastore.GetProperty("browser")
                    };
                }
            }
            return null;
        }

        private async Task<VimClient.ObjectContent[]> LoadReferenceTree(VsphereConnection connection)
        {
            var plan = new TraversalSpec
            {
                name = "FolderTraverseSpec",
                type = "Folder",
                path = "childEntity",
                selectSet = new SelectionSpec[] {

                    new TraversalSpec()
                    {
                        type = "Datacenter",
                        path = "datastore",
                        selectSet = new SelectionSpec[] {
                            new SelectionSpec {
                                name = "FolderTraverseSpec"
                            }
                        }
                    },

                    new TraversalSpec()
                    {
                        type = "Folder",
                        path = "childEntity",
                        selectSet = new SelectionSpec[] {
                            new SelectionSpec {
                                name = "FolderTraverseSpec"
                            }
                        }
                    }
                }
            };

            var props = new PropertySpec[]
            {
                new PropertySpec
                {
                    type = "Datastore",
                    pathSet = new string[] { "name", "browser" }
                }
            };

            ObjectSpec objectspec = new ObjectSpec();
            objectspec.obj = connection.Sic.rootFolder;
            objectspec.selectSet = new SelectionSpec[] { plan };

            PropertyFilterSpec filter = new PropertyFilterSpec();
            filter.propSet = props;
            filter.objectSet = new ObjectSpec[] { objectspec };

            PropertyFilterSpec[] filters = new PropertyFilterSpec[] { filter };
            RetrievePropertiesResponse response = await connection.Client.RetrievePropertiesAsync(connection.Props, filters);

            return response.returnval;
        }

        #endregion
    }
}
