// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.ServiceModel;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using VimClient;
using AutoMapper;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Domain.Vsphere.Extensions;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Exceptions;
using System.Web;

namespace Player.Vm.Api.Domain.Vsphere.Services
{
    public interface IVsphereService
    {
        Task<VsphereVirtualMachine> GetMachineById(Guid id);
        Task<string> GetConsoleUrl(VsphereVirtualMachine machine);
        Task<NicOptions> GetNicOptions(Guid id, bool canManage, Dictionary<string, string> allowedNetworks, VsphereVirtualMachine machine);
        Task<Dictionary<string, string>> GetVmNetworks(VsphereVirtualMachine machine, bool canManage, Dictionary<string, string> allowedNetworks);
        Task<string> GetConnectionAddress(Guid vmId);
        Task<string> PowerOnVm(Guid id);
        Task<string> PowerOffVm(Guid id);
        Task<string> RebootVm(Guid id);
        Task<string> ShutdownVm(Guid id);
        Task<TaskInfo> ReconfigureVm(Guid id, Feature feature, string label, string newvalue);
        Task<VirtualMachineToolsStatus> GetVmToolsStatus(Guid id);
        Task<string> UploadFileToVm(Guid id, string username, string password, string filepath, Stream fileStream);
        Task<string> GetVmFileUrl(Guid id, string username, string password, string filepath);
        Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListIsos(Guid? viewId = null);
        Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListIsosForVm(Guid vmId, Guid? viewId = null);
        Task<IsoOperationOutcome> UploadIso(string viewId, string scopeId, string filename, string localFilePath);
        Task<IsoOperationOutcome> DeleteIso(string viewId, string scopeId, string filename);
        int GetEnabledConnectionCount();
        Task<string> SetResolution(Guid id, int width, int height);
        Task<ManagedObjectReference[]> BulkPowerOperation(Guid[] ids, PowerOperation operation);
        Task<Dictionary<Guid, string>> BulkShutdown(Guid[] ids);
        Task<Dictionary<Guid, string>> BulkReboot(Guid[] ids);
        Task<Dictionary<Guid, PowerState>> GetPowerState(IEnumerable<Guid> machineIds);
        Task<IEnumerable<Event>> GetEvents(EventFilterSpec filterSpec, VsphereConnection connection);
        Task RevertToCurrentSnapshot(Guid vmId);
        Task<List<VmSnapshot>> GetSnapshots(Guid vmId);
        Task RevertToSnapshot(Guid vmId, string snapshotMoRefValue);
        Task<string> CreateSnapshot(Guid vmId, string snapshotName, string description, bool includeMemory);
        Task<string> DeleteSnapshot(Guid vmId, string snapshotMoRefValue);
        Task<GuestProcessResult> RunGuestProcess(Guid vmId, string username, string password, string command, string arguments, string workingDirectory, TimeSpan timeout);
        Task<long> RunGuestProcessFast(Guid vmId, string username, string password, string command, string arguments, string workingDirectory);
        Task<string> ReadGuestFile(Guid vmId, string username, string password, string guestFilePath);
    }

    public class VsphereService : IVsphereService
    {
        private RewriteHostOptions _rewriteHostOptions;

        private readonly ILogger<VsphereService> _logger;
        int _pollInterval = 1000;

        private readonly IConfiguration _configuration;
        private readonly IConnectionService _connectionService;
        private readonly IMapper _mapper;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly VsphereOptions _vsphereOptions;

        public VsphereService(
                IOptions<RewriteHostOptions> rewriteHostOptions,
                ILogger<VsphereService> logger,
                VsphereOptions vsphereOptions,
                IConfiguration configuration,
                IConnectionService connectionService,
                IMapper mapper,
                IHttpClientFactory httpClientFactory
            )
        {
            _rewriteHostOptions = rewriteHostOptions.Value;
            _logger = logger;
            _connectionService = connectionService;
            _configuration = configuration;
            _mapper = mapper;
            _httpClientFactory = httpClientFactory;
            _vsphereOptions = vsphereOptions;
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
            vmReference = aggregate?.MachineReference;

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
            vmReference = aggregate?.MachineReference;

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
                var vmReference = aggregate?.MachineReference;

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
            vmReference = aggregate?.MachineReference;

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
            vmReference = aggregate?.MachineReference;

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
                var vmReference = aggregate?.MachineReference;

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
                var vmReference = aggregate?.MachineReference;

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
            var vmReference = aggregate?.MachineReference;

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
                var vmReference = aggregate?.MachineReference;

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
            var vmReference = aggregate?.MachineReference;

            if (vmReference == null)
            {
                _logger.LogDebug($"could not get tools status, vmReference is null");
                return VirtualMachineToolsStatus.toolsNotRunning;
            }

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

            // GetVm returns null when the VM isn't in the connection cache yet (e.g. between connect-loop
            // refreshes), so null-condition the reference and bail with a clear, retryable message instead
            // of NRE-ing below.
            var vmReference = aggregate?.MachineReference;

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

                    using (var httpClientHandler = CreateGuestFileHttpClientHandler())
                    {
                        using (var httpClient = new HttpClient(httpClientHandler))
                        {
                            httpClient.DefaultRequestHeaders.Accept.Clear();
                            using (MemoryStream ms = new MemoryStream())
                            {
                                httpClient.Timeout = GetGuestFileTransferTimeout();
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
            var vmReference = aggregate?.MachineReference;

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
            var timeoutMinutes = _vsphereOptions.TaskTimeoutMinutes;
            var hasTimeout = timeoutMinutes > 0;
            var deadline = hasTimeout ? DateTime.UtcNow.AddMinutes(timeoutMinutes) : DateTime.MaxValue;

            var taskKey = task?.Value;
            TaskInfo info = new TaskInfo();
            int polls = 0;
            int missingTaskInfoPolls = 0;
            int missingTaskInfoTimeoutSeconds = _vsphereOptions.TaskInfoUnavailableTimeoutSeconds > 0
                ? _vsphereOptions.TaskInfoUnavailableTimeoutSeconds
                : 30;
            int maxMissingTaskInfoPolls = Math.Max(1, (int)Math.Ceiling(TimeSpan.FromSeconds(missingTaskInfoTimeoutSeconds).TotalMilliseconds / _pollInterval));

            //poll until the task reaches a terminal state or we hit the deadline
            do
            {
                await Task.Delay(_pollInterval);
                info = await GetVimTaskInfo(task, connection);
                polls++;

                if (info == null)
                {
                    missingTaskInfoPolls++;

                    if (missingTaskInfoPolls >= maxMissingTaskInfoPolls)
                    {
                        _logger.LogWarning(
                            "vCenter task {TaskKey} info was not available from the property collector within {TimeoutSeconds} second(s) after {Polls} polls. Abandoning wait.",
                            taskKey, missingTaskInfoTimeoutSeconds, missingTaskInfoPolls);
                        throw new TimeoutException(
                            $"vCenter task {taskKey} info was not available from the property collector within {missingTaskInfoTimeoutSeconds} second(s).");
                    }

                    info = new TaskInfo { state = TaskInfoState.running };
                }
                else
                {
                    missingTaskInfoPolls = 0;
                }

                if (hasTimeout && DateTime.UtcNow >= deadline &&
                    (info.state == TaskInfoState.running || info.state == TaskInfoState.queued))
                {
                    _logger.LogWarning(
                        "vCenter task {TaskKey} did not complete within {TimeoutMinutes} minute(s); last state {State}, progress {Progress}% after {Polls} polls. Abandoning wait.",
                        taskKey, timeoutMinutes, info.state, info.progress, polls);
                    throw new TimeoutException(
                        $"vCenter task {taskKey} did not reach a terminal state within {timeoutMinutes} minute(s) (last state: {info.state}).");
                }
            } while ((info.state == TaskInfoState.running || info.state == TaskInfoState.queued));

            _logger.LogDebug(
                "vCenter task {TaskKey} finished with state {State} after {Polls} polls.",
                taskKey, info.state, polls);

            return info;
        }

        private async Task<TaskInfo> GetVimTaskInfo(ManagedObjectReference task, VsphereConnection connection)
        {
            RetrievePropertiesResponse response = await connection.Client.RetrievePropertiesAsync(
                connection.Props,
                TaskFilter(task));

            // A task that has not yet been registered in the property collector (or a transient empty
            // read) returns no objects/props. Let the caller retry briefly without masking a task that
            // never becomes visible.
            var oc = response?.returnval;
            if (oc == null || oc.Length == 0 || oc[0].propSet == null || oc[0].propSet.Length == 0)
            {
                _logger.LogDebug("Task {TaskKey} info not yet available from property collector.", task?.Value);
                return null;
            }

            return (TaskInfo)oc[0].propSet[0].val;
        }

        public async Task<NicOptions> GetNicOptions(Guid id, bool canManage, Dictionary<string, string> allowedNetworks, VsphereVirtualMachine machine)
        {
            var available = await GetVmNetworks(machine, canManage, allowedNetworks);
            var current = await GetVMConfiguration(machine, Feature.net);
            var readOnly = new List<string>();

            if (!canManage)
            {
                var aggregate = await this.GetVm(id);
                List<Network> hostNetworks = _connectionService.GetNetworksByHost(machine.HostReference, aggregate.Connection.Address);

                foreach (var (_, networkRef) in current)
                {
                    if (!string.IsNullOrEmpty(networkRef) && !available.ContainsKey(networkRef))
                    {
                        var net = hostNetworks.FirstOrDefault(n =>
                            string.Equals(n.Reference, networkRef, StringComparison.OrdinalIgnoreCase));
                        if (net != null)
                        {
                            available[net.Reference] = net.Name;
                        }
                        readOnly.Add(networkRef);
                    }
                }
            }

            return new NicOptions
            {
                AvailableNetworks = available,
                CurrentNetworks = current,
                ReadOnlyNetworks = readOnly.ToArray()
            };
        }

        public async Task<string> GetConnectionAddress(Guid vmId)
        {
            var aggregate = await this.GetVm(vmId);
            return aggregate?.Connection?.Address;
        }

        public async Task<Dictionary<string, string>> GetVmNetworks(VsphereVirtualMachine machine, bool canManage, Dictionary<string, string> allowedNetworks)
        {
            var aggregate = await this.GetVm(machine.Id);
            List<Network> hostNetworks = _connectionService.GetNetworksByHost(machine.HostReference, aggregate.Connection.Address);

            if (canManage)
            {
                return hostNetworks.OrderBy(n => n.Name).ToDictionary(n => n.Reference, n => n.Name);
            }
            else
            {
                if (allowedNetworks != null)
                {
                    return hostNetworks
                        .Where(n => allowedNetworks.TryGetValue(n.Reference, out var storedName)
                            && (storedName == null || string.Equals(n.Name, storedName, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(n => n.Name)
                        .ToDictionary(n => n.Reference, n => n.Name);
                }
                else
                {
                    return new Dictionary<string, string>();
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
                                var card = backingInfo as VirtualEthernetCardDistributedVirtualPortBackingInfo;
                                var portGroupKey = card?.port?.portgroupKey;

                                if (!string.IsNullOrEmpty(portGroupKey))
                                {
                                    names.Add(deviceInfo.label, portGroupKey);
                                }
                            }
                            else if (backingInfo.GetType() == typeof(VirtualEthernetCardNetworkBackingInfo))
                            {
                                var card = backingInfo as VirtualEthernetCardNetworkBackingInfo;
                                var network = _connectionService.GetNetworkByName(card.deviceName, aggregate.Connection.Address);
                                names.Add(deviceInfo.label, network?.Reference ?? card.deviceName);
                            }
                        }
                    }
                    break;
                default:
                    throw new Exception("Invalid request.");
                    //break;
            }
            return names;
        }

        // Single source of truth for the datastore-relative ISO folder layout, shared by the ISO
        // search (GetIsos) and the datastore upload (UploadIsoToConnection) so they never diverge.
        private static string BuildIsoFolderRelative(string baseFolder, string viewId, string scopeId)
        {
            return $"{baseFolder}/{viewId}/{scopeId}";
        }

        // Lists ISOs for the management UI from an arbitrary connected host. The result is
        // display-only, so a single host is enough - a host that is missing an ISO means that upload
        // failed and should be retried, not that every list should fan out. For the VM mount picker
        // use ListIsosForVm instead: that result is actionable and must come from the VM's own host.
        // Passing null roots the search at the base folder so every View is enumerated at once
        // ("all views" mode); passing a viewId roots it at that View's folder.
        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListIsos(Guid? viewId = null)
        {
            var connection = _connectionService.GetAllConnections()
                .FirstOrDefault(c => c.Enabled && c.Connected && c.Client != null);

            if (connection == null)
            {
                _logger.LogError("No connected vSphere hosts available to list ISOs.");
                return new Dictionary<Guid, IReadOnlyList<IsoFile>>();
            }

            return await ListIsosFromConnection(connection, viewId);
        }

        // Lists ISOs visible to a specific VM, from the host that VM actually runs on. IsoFile.Path is
        // a host-specific "[dsName] baseFolder/..." string that is handed straight back to MountIso, so
        // on a multi-vCenter install with differing DsName/BaseFolder a path listed from some other
        // host would not resolve for this VM. Resolving the connection from the VM keeps the mountable
        // list and the mount itself on the same host.
        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListIsosForVm(Guid vmId, Guid? viewId = null)
        {
            var aggregate = await GetVm(vmId);

            if (aggregate?.Connection?.Client == null)
            {
                _logger.LogError("Could not resolve a vSphere connection for VM {VmId} to list ISOs.", vmId);
                return new Dictionary<Guid, IReadOnlyList<IsoFile>>();
            }

            return await ListIsosFromConnection(aggregate.Connection, viewId);
        }

        // Lists ISOs from one host's datastore in a SINGLE recursive datastore-browser task, keyed by
        // scope id (the leaf folder name). Shared by ListIsos and ListIsosForVm so the two entry points
        // only differ in how the connection is chosen, never in how the datastore is searched or parsed.
        private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IsoFile>>> ListIsosFromConnection(
            VsphereConnection connection, Guid? viewId)
        {
            var byScope = new Dictionary<Guid, IReadOnlyList<IsoFile>>();

            var dsName = connection.Host.DsName;
            // Root at the base folder for all Views, or the View folder for one View. Either way the
            // leaf folders are baseFolder/{viewId}/{scopeId}, so the scope id is always the last segment.
            var rootFolder = viewId.HasValue
                ? $"[{dsName}] {connection.Host.BaseFolder}/{viewId}"
                : $"[{dsName}] {connection.Host.BaseFolder}";

            var datastore = await GetDatastoreByName(dsName, connection);
            if (datastore == null)
            {
                _logger.LogError($"Datastore {dsName} not found in {connection.Address}.");
                return byScope;
            }

            // Server-side filter to ISO files (matches TopoMojo's "*" + extension pattern).
            var spec = new HostDatastoreBrowserSearchSpec { matchPattern = new[] { "*" + IsoFileNaming.Extension } };
            var results = new List<HostDatastoreBrowserSearchResults>();
            var task = await connection.Client.SearchDatastoreSubFolders_TaskAsync(datastore.Browser, rootFolder, spec);
            var info = await WaitForVimTask(task, connection);
            if (info.state == TaskInfoState.error)
            {
                if (info.error.fault != null &&
                    info.error.fault.ToString().Equals("FileNotFound", StringComparison.CurrentCultureIgnoreCase))
                {
                    // Root folder not present yet - no ISOs.
                    return byScope;
                }
                _logger.LogError(info.error.localizedMessage);
            }
            else if (info.result != null)
            {
                results.AddRange((HostDatastoreBrowserSearchResults[])info.result);
            }

            foreach (var result in results)
            {
                if (result?.file == null || result.file.Length == 0)
                    continue;

                // folderPath is "[ds] baseFolder/{viewId}/{scopeId}"; the scope id is the last segment.
                // Skip anything that isn't a scope (Guid) folder - e.g. a stray file in the base/View dir.
                var leaf = result.folderPath.TrimEnd('/').Split('/').Last();
                if (!Guid.TryParse(leaf, out var scopeId))
                    continue;

                if (!byScope.TryGetValue(scopeId, out var existing))
                {
                    existing = new List<IsoFile>();
                    byScope[scopeId] = existing;
                }

                ((List<IsoFile>)existing).AddRange(result.file.Select(x => new IsoFile(result.folderPath, x.path)));
            }

            return byScope;
        }

        public async Task<IsoOperationOutcome> UploadIso(string viewId, string scopeId, string filename, string localFilePath)
        {
            // Upload to all enabled/connected hosts so the ISO is available wherever the VM runs
            // (mirrors the shared-storage semantics of the NFS path).
            var connections = GetEnabledConnections();

            if (!connections.Any())
            {
                throw new InvalidOperationException("No connected vSphere hosts available for ISO upload.");
            }

            // Upload to every host concurrently; each host streams its own FileStream off the staged
            // file, so the PUTs are independent. Per-host failures are captured (never faulting the
            // whole batch) so wall-clock is ~the slowest host rather than the sum.
            // Idempotent re-upload heals any missed host (deterministic path + PUT overwrite), so we
            // report partial failures rather than aborting; AggregateHostResults throws only if all fail.
            var results = await Task.WhenAll(connections.Select(async connection =>
            {
                try
                {
                    await UploadIsoToConnection(connection, viewId, scopeId, filename, localFilePath);
                    return (string)null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to upload ISO {File} to {Host}", filename, connection.Address);
                    return $"{connection.Address}: {ex.Message}";
                }
            }));

            var (failedHostCount, totalHostCount) = AggregateHostResults(results, connections.Count, "upload", filename);

            return new IsoOperationOutcome
            {
                FailedHostCount = failedHostCount,
                TotalHostCount = totalHostCount
            };
        }

        // How many hosts an ISO write would currently fan out to. Reported by VsphereIsoProvider as its
        // target count so a wholly-failed provider can still be tallied honestly in the response.
        public int GetEnabledConnectionCount()
        {
            return GetEnabledConnections().Count;
        }

        // Enabled, connected hosts with a live client - the set ISO uploads/deletes fan out across.
        private List<VsphereConnection> GetEnabledConnections()
        {
            return _connectionService.GetAllConnections()
                .Where(c => c.Enabled && c.Connected && c.Client != null)
                .ToList();
        }

        // Tally per-host outcomes from a fan-out (a null entry is success; a non-null entry is a
        // server-side failure detail). Host addresses/reasons stay in the logs and are never returned
        // to the caller, so they can't leak to app users - callers receive counts only. Throws when
        // every host failed; logs a warning on partial failure.
        private (int failedHostCount, int totalHostCount) AggregateHostResults(
            IReadOnlyList<string> results, int totalHostCount, string operation, string filename)
        {
            var failures = results.Where(r => r != null).ToList();

            if (failures.Count == totalHostCount)
            {
                _logger.LogError("ISO {File} failed to {Operation} on all {Count} hosts: {Failures}", filename, operation, totalHostCount, string.Join("; ", failures));
                throw new Exception($"ISO {operation} failed on all hosts. Contact an administrator.");
            }

            if (failures.Any())
            {
                _logger.LogWarning("ISO {File} {Operation} completed with partial failures: {Failures}", filename, operation, string.Join("; ", failures));
            }

            return (failures.Count, totalHostCount);
        }

        private async Task UploadIsoToConnection(VsphereConnection connection, string viewId, string scopeId, string filename, string localFilePath)
        {
            // Resolve the datastore HTTP file API target (and create the destination directory).
            var client = await BuildDatastoreFileClient(connection, viewId, scopeId, filename, ensureDirectory: true);

            using (var fileStream = System.IO.File.OpenRead(localFilePath))
            {
                var content = new StreamContent(fileStream);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentLength = fileStream.Length;

                _logger.LogDebug("Uploading ISO to datastore: {Url}", client.BaseAddress);

                // Empty relative URI resolves to BaseAddress (the full /folder/...?dcPath=&dsName= URL).
                var response = await client.PutAsync("", content);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Datastore PUT failed ({(int)response.StatusCode}): {body}");
                }
            }
        }

        // Resolves dsName/folderPath/datacenter (incl. null-guard throw), optionally creates the
        // destination directory (upload only), builds the datastore HTTP file API URL, and returns a
        // "vSphereDatastore" HttpClient with that URL as BaseAddress and the per-host Basic-auth header
        // set. Shared by ISO upload (PUT) and delete (DELETE) so the path/auth setup never diverges.
        private async Task<HttpClient> BuildDatastoreFileClient(VsphereConnection connection, string viewId, string scopeId, string filename, bool ensureDirectory)
        {
            var dsName = connection.Host.DsName;

            // datastore-relative folder (no "[ds]" prefix; that form is only used for search/mount/MakeDirectory paths)
            var folderPath = BuildIsoFolderRelative(connection.Host.BaseFolder, viewId, scopeId);

            var datacenter = await GetDatacenterForDatastore(dsName, connection);
            if (datacenter == null)
            {
                throw new InvalidOperationException($"Could not resolve datacenter for datastore {dsName} on {connection.Address}");
            }

            if (ensureDirectory)
            {
                // ensure the destination directory exists (ignore "already exists")
                await EnsureDatastoreDirectory(connection, datacenter, dsName, folderPath);
            }

            var url = BuildDatastoreUploadUrl(connection.Address, folderPath, filename, datacenter.Name, dsName);

            var client = _httpClientFactory.CreateClient("vSphereDatastore");
            client.BaseAddress = new Uri(url);

            // HTTP Basic auth (not the SOAP session cookie) - matches the topomojo implementation.
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{connection.Host.Username}:{connection.Host.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            return client;
        }

        private static string BuildDatastoreUploadUrl(string host, string folderPath, string file, string dcName, string dsName)
        {
            // https://{host}/folder/{folder}/{file}?dcPath={datacenter}&dsName={datastore}
            var folder = string.Join("/", folderPath.Split('/').Select(Uri.EscapeDataString));
            return $"https://{host}/folder/{folder}/{Uri.EscapeDataString(file)}"
                 + $"?dcPath={Uri.EscapeDataString(dcName)}&dsName={Uri.EscapeDataString(dsName)}";
        }

        public async Task<IsoOperationOutcome> DeleteIso(string viewId, string scopeId, string filename)
        {
            // Delete from all enabled/connected hosts so the ISO is removed wherever it was uploaded
            // (mirrors UploadIso, which writes to every host).
            var connections = GetEnabledConnections();

            if (!connections.Any())
            {
                throw new InvalidOperationException("No connected vSphere hosts available for ISO delete.");
            }

            // Delete on every host concurrently; per-host failures are captured (never faulting the
            // whole batch). A missing file is treated as success inside DeleteIsoFromConnection.
            var results = await Task.WhenAll(connections.Select(async connection =>
            {
                try
                {
                    await DeleteIsoFromConnection(connection, viewId, scopeId, filename);
                    return (string)null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete ISO {File} from {Host}", filename, connection.Address);
                    return $"{connection.Address}: {ex.Message}";
                }
            }));

            var (failedHostCount, totalHostCount) = AggregateHostResults(results, connections.Count, "delete", filename);

            return new IsoOperationOutcome
            {
                FailedHostCount = failedHostCount,
                TotalHostCount = totalHostCount
            };
        }

        private async Task DeleteIsoFromConnection(VsphereConnection connection, string viewId, string scopeId, string filename)
        {
            // Same /folder/...?dcPath=&dsName= URL as the PUT upload; the datastore HTTP file API
            // accepts DELETE on it. No directory creation needed for a delete.
            var client = await BuildDatastoreFileClient(connection, viewId, scopeId, filename, ensureDirectory: false);

            _logger.LogDebug("Deleting ISO from datastore: {Url}", client.BaseAddress);

            // Empty relative URI resolves to BaseAddress (the full /folder/...?dcPath=&dsName= URL).
            var response = await client.DeleteAsync("");

            // Already gone => idempotent success (lets a re-delete or partially-uploaded ISO clear cleanly).
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new Exception($"Datastore DELETE failed ({(int)response.StatusCode}): {body}");
            }
        }

        private async Task EnsureDatastoreDirectory(VsphereConnection connection, DatacenterInfo datacenter, string dsName, string folderPath)
        {
            var datastorePath = $"[{dsName}] {folderPath}";
            try
            {
                await connection.Client.MakeDirectoryAsync(
                    connection.Sic.fileManager,
                    datastorePath,
                    datacenter.Reference,
                    true /* createParentDirectories */);
            }
            catch (FaultException<VimClient.FileAlreadyExists>)
            {
                // directory already present - nothing to do
            }
            catch (FaultException<VimClient.FileFault> ex) when (ex.Detail is VimClient.FileAlreadyExists)
            {
                // directory already present (surfaced as the base FileFault) - nothing to do
            }
        }

        private async Task<DatacenterInfo> GetDatacenterForDatastore(string dsName, VsphereConnection connection)
        {
            var tree = await LoadInventoryTree(connection, new PropertySpec[]
            {
                new PropertySpec
                {
                    type = "Datacenter",
                    pathSet = new string[] { "name", "datastore" }
                },
                new PropertySpec
                {
                    type = "Datastore",
                    pathSet = new string[] { "name" }
                }
            });
            if (tree.Length == 0)
            {
                return null;
            }

            var datacenters = tree.FindType("Datacenter");
            var datastores = tree.FindType("Datastore");

            // Return the datacenter that actually contains the named datastore.
            foreach (var dc in datacenters)
            {
                var dsRefs = dc.GetProperty("datastore") as ManagedObjectReference[] ?? Array.Empty<ManagedObjectReference>();

                var match = datastores.FirstOrDefault(d =>
                    (d.GetProperty("name") as string) == dsName &&
                    dsRefs.Any(r => r.Value == d.obj.Value));

                if (match != null)
                {
                    return ToDatacenterInfo(dc);
                }
            }

            // No datacenter contains the configured datastore. Return null rather than guessing the
            // first datacenter (which would upload against the wrong dcPath); the caller surfaces this
            // as a clear per-host failure so a misconfigured DsName / stale inventory is visible.
            return null;
        }

        private static DatacenterInfo ToDatacenterInfo(VimClient.ObjectContent dc)
        {
            return new DatacenterInfo
            {
                Name = dc.GetProperty("name") as string,
                Reference = dc.obj
            };
        }

        // Walk the rootFolder inventory (Datacenters and their datastores, recursing through folders)
        // retrieving the supplied properties. Shared by datastore and datacenter lookups so the
        // traversal scaffolding lives in one place.
        private async Task<VimClient.ObjectContent[]> LoadInventoryTree(VsphereConnection connection, PropertySpec[] props)
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

            ObjectSpec objectspec = new ObjectSpec
            {
                obj = connection.Sic.rootFolder,
                selectSet = new SelectionSpec[] { plan }
            };

            PropertyFilterSpec filter = new PropertyFilterSpec
            {
                propSet = props,
                objectSet = new ObjectSpec[] { objectspec }
            };

            PropertyFilterSpec[] filters = new PropertyFilterSpec[] { filter };
            RetrievePropertiesResponse response = await connection.Client.RetrievePropertiesAsync(connection.Props, filters);

            return response.returnval;
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
                        Network network = _connectionService.GetNetworkByReference(newvalue, aggregate.Connection.Address);

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
                                deviceName = network.Name
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
            var vmReference = aggregate?.MachineReference;

            if (vmReference == null)
            {
                _logger.LogDebug($"Could not get vm reference");
                return "error";
            }

            string state = await GetPowerState(id);

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

        public async Task<List<VmSnapshot>> GetSnapshots(Guid vmId)
        {
            var aggregate = await this.GetVm(vmId);
            var machineReference = aggregate.MachineReference;

            RetrievePropertiesResponse propertiesResponse = await aggregate.Connection.Client.RetrievePropertiesAsync(
                aggregate.Connection.Props,
                VmFilter(machineReference, "snapshot"));

            VimClient.ObjectContent vm = propertiesResponse.returnval.FirstOrDefault();
            var snapshotInfo = vm?.GetProperty("snapshot") as VirtualMachineSnapshotInfo;

            if (snapshotInfo?.rootSnapshotList == null)
                return new List<VmSnapshot>();

            var currentSnapshotValue = snapshotInfo.currentSnapshot?.Value;
            return FlattenSnapshotTree(snapshotInfo.rootSnapshotList, currentSnapshotValue, 0);
        }

        private List<VmSnapshot> FlattenSnapshotTree(VirtualMachineSnapshotTree[] trees, string currentSnapshotValue, int depth)
        {
            var snapshots = new List<VmSnapshot>();
            if (trees == null) return snapshots;

            foreach (var tree in trees)
            {
                snapshots.Add(new VmSnapshot
                {
                    Id = tree.snapshot.Value,
                    Name = tree.name,
                    Description = tree.description,
                    CreateTime = tree.createTime,
                    State = tree.state.ToString(),
                    IsCurrent = tree.snapshot.Value == currentSnapshotValue,
                    Depth = depth
                });

                snapshots.AddRange(FlattenSnapshotTree(tree.childSnapshotList, currentSnapshotValue, depth + 1));
            }

            return snapshots;
        }

        public async Task RevertToSnapshot(Guid vmId, string snapshotIdentifier)
        {
            var aggregate = await this.GetVm(vmId);
            var snapshotMoRefValue = await ResolveSnapshotMoRefValue(vmId, snapshotIdentifier);
            _logger.LogInformation(
                "RevertToSnapshot vm {vmId} reverting to snapshot identifier '{Identifier}' resolved to MoRef '{MoRef}'",
                vmId, snapshotIdentifier, snapshotMoRefValue);
            var snapshotRef = new ManagedObjectReference { type = "VirtualMachineSnapshot", Value = snapshotMoRefValue };
            var task = await aggregate.Connection.Client.RevertToSnapshot_TaskAsync(snapshotRef, null, false);
            var taskInfo = await WaitForVimTask(task, aggregate.Connection);

            if (taskInfo.state == TaskInfoState.error)
            {
                var message = taskInfo.error?.localizedMessage ?? "Revert to snapshot task failed";
                _logger.LogError("RevertToSnapshot task for vm {vmId} failed: {Message}", vmId, message);
                throw new Exception(message);
            }

            _logger.LogInformation("RevertToSnapshot vm {vmId} reverted to snapshot '{MoRef}'", vmId, snapshotMoRefValue);
        }

        public async Task<string> CreateSnapshot(Guid vmId, string snapshotName, string description, bool includeMemory)
        {
            var aggregate = await this.GetVm(vmId);
            // quiesce=false matches the existing RevertToSnapshot conservatism — let the caller add VM Tools quiesce later if needed.
            var task = await aggregate.Connection.Client.CreateSnapshot_TaskAsync(
                aggregate.MachineReference, snapshotName, description, includeMemory, false);
            var taskInfo = await WaitForVimTask(task, aggregate.Connection);

            if (taskInfo.state == TaskInfoState.error)
                throw new Exception(taskInfo.error.localizedMessage);

            return $"snapshot {snapshotName} created on vm {vmId}";
        }

        public async Task<string> DeleteSnapshot(Guid vmId, string snapshotIdentifier)
        {
            var aggregate = await this.GetVm(vmId);
            var snapshotMoRefValue = await ResolveSnapshotMoRefValue(vmId, snapshotIdentifier);
            var snapshotRef = new ManagedObjectReference { type = "VirtualMachineSnapshot", Value = snapshotMoRefValue };
            // removeChildren=false: only the named snapshot is removed; its children are reparented onto its parent.
            var task = await aggregate.Connection.Client.RemoveSnapshot_TaskAsync(snapshotRef, false, false);
            var taskInfo = await WaitForVimTask(task, aggregate.Connection);

            if (taskInfo.state == TaskInfoState.error)
                throw new Exception(taskInfo.error.localizedMessage);

            return $"snapshot {snapshotIdentifier} removed from vm {vmId}";
        }

        /// <summary>
        /// Resolve a caller-supplied snapshot identifier to a vSphere snapshot managed object reference
        /// value (e.g. "snapshot-1234"). Steamfitter and other callers may pass either the snapshot's
        /// human-readable name or its MoRef value, so we look the identifier up against the VM's snapshot
        /// tree by name first, then fall back to treating it as a MoRef value. Matching by name is
        /// case-sensitive and, when multiple snapshots share a name, resolves to the most recently created.
        /// </summary>
        private async Task<string> ResolveSnapshotMoRefValue(Guid vmId, string snapshotIdentifier)
        {
            if (string.IsNullOrWhiteSpace(snapshotIdentifier))
                throw new ArgumentException("A snapshot name or id must be provided.", nameof(snapshotIdentifier));

            var identifier = snapshotIdentifier.Trim();
            var snapshots = await GetSnapshots(vmId);

            // Prefer an exact MoRef match so callers that already pass the id keep working unambiguously.
            var byId = snapshots.FirstOrDefault(s => s.Id == identifier);
            if (byId != null)
                return byId.Id;

            // Otherwise resolve by name. Case-insensitive + trimmed to tolerate caller formatting; when
            // multiple snapshots share a name, prefer the most recently created.
            var byName = snapshots
                .Where(s => string.Equals(s.Name?.Trim(), identifier, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.CreateTime)
                .ToList();

            if (byName.Count > 0)
                return byName[0].Id;

            // No match. Fail with a clear, actionable message (and the available snapshots) instead of
            // passing an unresolvable value to vSphere, which returns a cryptic
            // "has already been deleted or has not been completely created" fault.
            var available = snapshots.Count == 0
                ? "none"
                : string.Join(", ", snapshots.Select(s => $"'{s.Name}' (id {s.Id})"));
            _logger.LogWarning(
                "ResolveSnapshotMoRefValue: no snapshot matching '{Identifier}' on vm {vmId}. Available snapshots: {Available}",
                identifier, vmId, available);
            throw new EntityNotFoundException<VmSnapshot>(
                $"No snapshot named or identified by '{snapshotIdentifier}' was found on this vm. Available snapshots: {available}.");
        }

        #region Guest Operations

        private async Task<(VsphereAggregate Aggregate, NamePasswordAuthentication Auth, ManagedObjectReference ProcessManager, ManagedObjectReference FileManager)> PrepareGuestOperations(Guid vmId, string username, string password)
        {
            var aggregate = await GetVm(vmId);
            if (aggregate?.MachineReference == null)
                throw new Exception("Could not get vm reference");

            var toolsStatus = await GetVmToolsStatus(vmId);
            if (toolsStatus == VirtualMachineToolsStatus.toolsNotInstalled || toolsStatus == VirtualMachineToolsStatus.toolsNotRunning)
                throw new Exception("VM Tools is not running");

            var auth = new NamePasswordAuthentication
            {
                interactiveSession = false,
                username = username,
                password = password
            };

            var processManager = new ManagedObjectReference
            {
                type = "GuestProcessManager",
                Value = "guestOperationsProcessManager"
            };

            var fileManager = new ManagedObjectReference
            {
                type = "GuestFileManager",
                Value = "guestOperationsFileManager"
            };

            return (aggregate, auth, processManager, fileManager);
        }

        public async Task<long> RunGuestProcessFast(Guid vmId, string username, string password, string command, string arguments, string workingDirectory)
        {
            _logger.LogDebug("RunGuestProcessFast called for vm {vmId}", vmId);

            var (aggregate, auth, processManager, _) = await PrepareGuestOperations(vmId, username, password);

            var spec = new GuestProgramSpec
            {
                programPath = command,
                arguments = arguments ?? string.Empty,
                workingDirectory = workingDirectory ?? string.Empty
            };

            return await aggregate.Connection.Client.StartProgramInGuestAsync(processManager, aggregate.MachineReference, auth, spec);
        }

        public async Task<GuestProcessResult> RunGuestProcess(Guid vmId, string username, string password, string command, string arguments, string workingDirectory, TimeSpan timeout)
        {
            _logger.LogDebug("RunGuestProcess called for vm {vmId}", vmId);

            var capturedStdoutPath = CombineGuestTempPath($"player-vm-api-stdout-{Guid.NewGuid():N}");
            var redirectedArgs = $"{arguments ?? string.Empty} > {capturedStdoutPath} 2>&1".Trim();

            var (aggregate, auth, processManager, fileManager) = await PrepareGuestOperations(vmId, username, password);

            var spec = new GuestProgramSpec
            {
                programPath = command,
                arguments = redirectedArgs,
                workingDirectory = workingDirectory ?? string.Empty
            };

            long pid = await aggregate.Connection.Client.StartProgramInGuestAsync(processManager, aggregate.MachineReference, auth, spec);

            var deadline = DateTime.UtcNow.Add(timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : timeout);
            GuestProcessInfo procInfo = null;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(_pollInterval);
                var procResponse = await aggregate.Connection.Client.ListProcessesInGuestAsync(processManager, aggregate.MachineReference, auth, new[] { pid });
                procInfo = procResponse?.returnval?.FirstOrDefault();

                if (procInfo == null || procInfo.endTimeSpecified)
                    break;
            }

            if (procInfo != null && !procInfo.endTimeSpecified)
            {
                return new GuestProcessResult
                {
                    Success = false,
                    ExitCode = -1,
                    Error = $"Timed out waiting for process {pid} to exit"
                };
            }

            string output = string.Empty;
            try
            {
                output = await ReadGuestFileInternal(aggregate, fileManager, auth, capturedStdoutPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read captured stdout file for vm {vmId}", vmId);
            }

            return new GuestProcessResult
            {
                Output = output,
                ExitCode = procInfo?.exitCode ?? 0,
                Success = procInfo != null && procInfo.exitCode == 0
            };
        }

        public async Task<string> ReadGuestFile(Guid vmId, string username, string password, string guestFilePath)
        {
            _logger.LogDebug("ReadGuestFile called for vm {vmId}", vmId);

            var (aggregate, auth, _, fileManager) = await PrepareGuestOperations(vmId, username, password);
            return await ReadGuestFileInternal(aggregate, fileManager, auth, guestFilePath);
        }

        private async Task<string> ReadGuestFileInternal(VsphereAggregate aggregate, ManagedObjectReference fileManager, NamePasswordAuthentication auth, string guestFilePath)
        {
            var transferInfo = await aggregate.Connection.Client.InitiateFileTransferFromGuestAsync(
                fileManager, aggregate.MachineReference, auth, guestFilePath);

            var url = transferInfo.url;

            // Replace IP with hostname when needed (mirrors UploadFileToVm)
            var summaryResponse = await aggregate.Connection.Client.RetrievePropertiesAsync(
                aggregate.Connection.Props,
                VmFilter(aggregate.MachineReference));
            var vmSummary = (VirtualMachineSummary)summaryResponse.returnval[0].propSet[0].val;

            var hostResponse = await aggregate.Connection.Client.RetrievePropertiesAsync(
                aggregate.Connection.Props,
                HostFilter(vmSummary.runtime.host, "name"));
            var hostName = hostResponse.returnval[0].propSet[0].val as string;

            if (!url.Contains(hostName))
            {
                url = url.Replace("https://", "");
                var s = url.IndexOf("/");
                url = "https://" + hostName + url.Substring(s);
            }

            using var handler = CreateGuestFileHttpClientHandler();
            using var client = new HttpClient(handler);
            client.Timeout = GetGuestFileTransferTimeout();

            return await client.GetStringAsync(url);
        }

        private HttpClientHandler CreateGuestFileHttpClientHandler()
        {
            var handler = new HttpClientHandler();
            if (_vsphereOptions.SkipGuestFileCertificateValidation)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

            return handler;
        }

        private TimeSpan GetGuestFileTransferTimeout()
        {
            return _vsphereOptions.GuestFileTransferTimeoutMinutes > 0
                ? TimeSpan.FromMinutes(_vsphereOptions.GuestFileTransferTimeoutMinutes)
                : Timeout.InfiniteTimeSpan;
        }

        private string CombineGuestTempPath(string fileName)
        {
            var tempPath = string.IsNullOrWhiteSpace(_vsphereOptions.GuestProcessTempPath)
                ? Path.GetTempPath()
                : _vsphereOptions.GuestProcessTempPath;

            tempPath = tempPath.TrimEnd('/', '\\');
            var separator = tempPath.Contains('\\') ? "\\" : "/";
            return $"{tempPath}{separator}{fileName}";
        }

        #endregion

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

            if (aggregate == null)
            {
                return null;
            }

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
            return await LoadInventoryTree(connection, new PropertySpec[]
            {
                new PropertySpec
                {
                    type = "Datastore",
                    pathSet = new string[] { "name", "browser" }
                }
            });
        }

        #endregion
    }
}
