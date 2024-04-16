using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Vsphere.Extensions;
using Player.Vm.Api.Domain.Vsphere.Options;
using VimClient;

namespace Player.Vm.Api.Domain.Vsphere.Models;

public class VsphereConnection
{
    public VimPortTypeClient Client;
    public ServiceContent Sic;
    public UserSession Session;
    public ManagedObjectReference Props;
    public string Address
    {
        get
        {
            return Host.Address;
        }
    }
    public bool Enabled
    {
        get
        {
            return Host.Enabled;
        }
    }

    public VsphereHost Host;
    public VsphereOptions Options;
    private ILogger _logger;
    private int _count = 0;
    private bool _forceReload = false;
    private object _lock = new object();

    public ConcurrentDictionary<Guid, ManagedObjectReference> MachineCache = new ConcurrentDictionary<Guid, ManagedObjectReference>();
    public ConcurrentDictionary<string, List<Network>> NetworkCache = new ConcurrentDictionary<string, List<Network>>();
    public ConcurrentDictionary<string, Datastore> DatastoreCache = new ConcurrentDictionary<string, Datastore>();
    public ConcurrentDictionary<string, Guid> VmGuids = new ConcurrentDictionary<string, Guid>();

    public VsphereConnection(VsphereHost host, VsphereOptions options, ILogger logger)
    {
        Host = host;
        Options = options;
        _logger = logger;
    }

    public async Task<IEnumerable<VsphereVirtualMachine>> Load()
    {
        var machineCache = Enumerable.Empty<VsphereVirtualMachine>();

        try
        {
            _logger.LogInformation($"Starting Connect Loop for {Host.Address} at {DateTime.UtcNow}");

            if (!Host.Enabled)
            {
                _logger.LogInformation("Vsphere disabled, skipping");
            }
            else
            {
                await Connect();

                if (_count == Options.LoadCacheAfterIterations)
                {
                    _count = 0;
                }

                if (_count == 0 || _forceReload)
                {
                    lock (_lock)
                    {
                        _forceReload = false;
                        _count = 0;
                    }

                    machineCache = await LoadCache();
                }

                _logger.LogInformation($"Finished Connect Loop for {Host.Address} at {DateTime.UtcNow} with {MachineCache.Count()} Machines");
                _count++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception encountered in ConnectionService loop");
            _count = 0;
        }

        return machineCache;
    }

    #region Connection Handling

    private async Task Connect()
    {
        // check whether session is expiring
        if (Session != null && (DateTime.Compare(DateTime.UtcNow, Session.lastActiveTime.AddMinutes(Options.ConnectionRefreshIntervalMinutes)) >= 0))
        {
            _logger.LogDebug("Connect():  Session is more than 20 minutes old");

            // renew session because it expires at 30 minutes (maybe 120 minutes on newer vc)
            _logger.LogInformation($"Connect():  renewing connection to {Host.Address}...[{Host.Username}]");
            try
            {
                var client = new VimPortTypeClient(VimPortTypeClient.EndpointConfiguration.VimPort, $"https://{Host.Address}/sdk");
                var sic = await client.RetrieveServiceContentAsync(new ManagedObjectReference { type = "ServiceInstance", Value = "ServiceInstance" });
                var props = sic.propertyCollector;
                var session = await client.LoginAsync(sic.sessionManager, Host.Username, Host.Password, null);

                var oldClient = Client;
                Client = client;
                Sic = sic;
                Props = props;
                Session = session;

                await oldClient.CloseAsync();
                oldClient.Dispose();
            }
            catch (Exception ex)
            {
                // no connection: Failed with Object reference not set to an instance of an object
                _logger.LogError(0, ex, $"Connect():  Failed with " + ex.Message);
                _logger.LogError(0, ex, $"Connect():  User: " + Host.Username);
                Disconnect();
            }
        }

        if (Client != null && Client.State == CommunicationState.Opened)
        {
            _logger.LogDebug("Connect():  CommunicationState.Opened");
            ServiceContent sic = Sic;
            UserSession session = Session;
            bool isNull = false;

            if (Sic == null)
            {
                sic = await ConnectToHost(Client);
                isNull = true;
            }

            if (Session == null)
            {
                session = await ConnectToSession(Client, sic);
                isNull = true;
            }

            if (isNull)
            {
                Session = session;
                Props = sic.propertyCollector;
                Sic = sic;
            }

            try
            {
                var x = await Client.RetrieveServiceContentAsync(new ManagedObjectReference { type = "ServiceInstance", Value = "ServiceInstance" });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking vcenter connection. Disconnecting.");
                Disconnect();
            }
        }

        if (Client != null && Client.State == CommunicationState.Faulted)
        {
            _logger.LogDebug($"Connect():  https://{Host.Address}/sdk CommunicationState is Faulted.");
            Disconnect();
        }

        if (Client == null)
        {
            try
            {
                _logger.LogDebug($"Connect():  Instantiating client https://{Host.Address}/sdk");
                var client = new VimPortTypeClient(VimPortTypeClient.EndpointConfiguration.VimPort, $"https://{Host.Address}/sdk");
                _logger.LogDebug($"Connect():  client: [{Client}]");

                var sic = await ConnectToHost(client);
                var session = await ConnectToSession(client, sic);

                Session = session;
                Props = sic.propertyCollector;
                Sic = sic;
                Client = client;
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Connect():  Failed with " + ex.Message);
            }
        }
    }

    private async Task<ServiceContent> ConnectToHost(VimPortTypeClient client)
    {
        _logger.LogInformation($"Connect():  Connecting to {Host.Address}...");
        var sic = await client.RetrieveServiceContentAsync(new ManagedObjectReference { type = "ServiceInstance", Value = "ServiceInstance" });
        return sic;
    }

    private async Task<UserSession> ConnectToSession(VimPortTypeClient client, ServiceContent sic)
    {
        _logger.LogInformation($"Connect():  logging into {Host.Address}...[{Host.Username}]");
        var session = await client.LoginAsync(sic.sessionManager, Host.Username, Host.Password, null);
        _logger.LogInformation($"Connect():  Session created.");
        return session;
    }

    public void Disconnect()
    {
        _logger.LogInformation($"Disconnect()");
        Client.Dispose();
        Client = null;
        Sic = null;
        Session = null;
    }

    #endregion

    #region Cache Setup

    private async Task<IEnumerable<VsphereVirtualMachine>> LoadCache()
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
                        path = "networkFolder",
                        selectSet = new SelectionSpec[] {
                            new SelectionSpec {
                                name = "FolderTraverseSpec"
                            }
                        }
                    },

                    new TraversalSpec()
                    {
                        type = "Datacenter",
                        path = "vmFolder",
                        selectSet = new SelectionSpec[] {
                            new SelectionSpec {
                                name = "FolderTraverseSpec"
                            }
                        }
                    },

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
                    },
                }
        };

        var props = new PropertySpec[]
        {
                new PropertySpec
                {
                    type = "DistributedVirtualSwitch",
                    pathSet = new string[] { "name", "uuid", "config.uplinkPortgroup" }
                },

                new PropertySpec
                {
                    type = "DistributedVirtualPortgroup",
                    pathSet = new string[] { "name", "host", "config.distributedVirtualSwitch" }
                },

                new PropertySpec
                {
                    type = "Network",
                    pathSet = new string[] { "name", "host" }
                },

                new PropertySpec
                {
                    type = "VirtualMachine",
                    pathSet = new string[] { "name", "config.uuid", "summary.runtime.powerState", "guest.net" }
                },

                new PropertySpec
                {
                    type = "Datastore",
                    pathSet = new string[] { "name", "browser" }
                }
        };

        ObjectSpec objectspec = new ObjectSpec
        {
            obj = Sic.rootFolder,
            selectSet = new SelectionSpec[] { plan }
        };

        PropertyFilterSpec filter = new PropertyFilterSpec
        {
            propSet = props,
            objectSet = new ObjectSpec[] { objectspec }
        };

        PropertyFilterSpec[] filters = new PropertyFilterSpec[] { filter };

        _logger.LogInformation($"Starting RetrieveProperties at {DateTime.UtcNow}");
        RetrievePropertiesResponse response = await Client.RetrievePropertiesAsync(Props, filters);
        _logger.LogInformation($"Finished RetrieveProperties at {DateTime.UtcNow}");

        _logger.LogInformation($"Starting LoadMachineCache at {DateTime.UtcNow}");
        var machineCache = LoadMachineCache(response.returnval.FindType("VirtualMachine"));
        _logger.LogInformation($"Finished LoadMachineCache at {DateTime.UtcNow}");

        _logger.LogInformation($"Starting LoadNetworkCache at {DateTime.UtcNow}");
        LoadNetworkCache(
            response.returnval.FindType("DistributedVirtualSwitch"),
            response.returnval.Where(o => o.obj.type.EndsWith("Network") || o.obj.type.EndsWith("DistributedVirtualPortgroup")).ToArray());
        _logger.LogInformation($"Finished LoadNetworkCache at {DateTime.UtcNow}");

        _logger.LogInformation($"Starting LoadDatastoreCache at {DateTime.UtcNow}");
        LoadDatastoreCache(response.returnval.FindType("Datastore"));
        _logger.LogInformation($"Finished LoadDatastoreCache at {DateTime.UtcNow}");

        return machineCache;
    }

    private IEnumerable<VsphereVirtualMachine> LoadMachineCache(VimClient.ObjectContent[] virtualMachines)
    {
        IEnumerable<Guid> existingMachineIds = MachineCache.Keys;
        List<Guid> currentMachineIds = new List<Guid>();
        List<VsphereVirtualMachine> vsphereVirtualMachines = new List<VsphereVirtualMachine>();

        foreach (var vm in virtualMachines)
        {
            string name = string.Empty;

            try
            {
                name = vm.GetProperty("name") as string;

                var idObj = vm.GetProperty("config.uuid");

                if (idObj == null)
                {
                    _logger.LogError($"Unable to load machine {name} - {vm.obj.Value}. Invalid UUID");
                    continue;
                }

                var toolsStatus = vm.GetProperty("summary.guest.toolsStatus") as Nullable<VirtualMachineToolsStatus>;
                VirtualMachineToolsStatus vmToolsStatus = VirtualMachineToolsStatus.toolsNotRunning;
                if (toolsStatus != null)
                {
                    vmToolsStatus = toolsStatus.Value;
                }

                var guid = Guid.Parse(idObj as string);
                var virtualMachine = new VsphereVirtualMachine
                {
                    //HostReference = ((ManagedObjectReference)vm.GetProperty("summary.runtime.host")).Value,
                    Id = guid,
                    Name = name,
                    Reference = vm.obj,
                    State = (VirtualMachinePowerState)vm.GetProperty("summary.runtime.powerState") == VirtualMachinePowerState.poweredOn ? "on" : "off",
                    VmToolsStatus = vmToolsStatus,
                    IpAddresses = ((GuestNicInfo[])vm.GetProperty("guest.net")).Where(x => x.ipAddress != null).SelectMany(x => x.ipAddress).ToArray()
                };

                vsphereVirtualMachines.Add(virtualMachine);

                MachineCache.AddOrUpdate(virtualMachine.Id, virtualMachine.Reference, (k, v) => v = virtualMachine.Reference);
                currentMachineIds.Add(virtualMachine.Id);
                VmGuids.AddOrUpdate(vm.obj.Value, guid, (k, v) => (v = guid));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error refreshing Virtual Machine {name} - {vm.obj.Value}");
            }
        }

        foreach (Guid existingId in existingMachineIds.Except(currentMachineIds))
        {
            if (MachineCache.TryRemove(existingId, out ManagedObjectReference stale))
            {
                _logger.LogDebug($"removing stale cache entry {stale.Value}");
            }
        }

        return vsphereVirtualMachines;
    }

    private void LoadNetworkCache(VimClient.ObjectContent[] distributedSwitches, VimClient.ObjectContent[] networks)
    {
        Dictionary<string, List<Network>> networkCache = new Dictionary<string, List<Network>>();
        IEnumerable<string> existingHosts = NetworkCache.Keys;
        List<string> currentHosts = new List<string>();

        foreach (var net in networks)
        {
            string name = null;

            try
            {
                name = net.GetProperty("name") as string;
                Network network = null;

                if (net.obj.type == "Network")
                {
                    network = new Network
                    {
                        IsDistributed = false,
                        Name = name,
                        SwitchId = null
                    };
                }
                else if (net.obj.type == "DistributedVirtualPortgroup")
                {
                    var dSwitchReference = net.GetProperty("config.distributedVirtualSwitch") as ManagedObjectReference;
                    var dSwitch = distributedSwitches.Where(x => x.obj.Value == dSwitchReference.Value).FirstOrDefault();

                    if (dSwitch != null)
                    {
                        var uplinkPortgroups = dSwitch.GetProperty("config.uplinkPortgroup") as ManagedObjectReference[];
                        if (uplinkPortgroups.Select(x => x.Value).Contains(net.obj.Value))
                        {
                            // Skip uplink portgroups
                            continue;
                        }
                        else
                        {
                            network = new Network
                            {
                                IsDistributed = true,
                                Name = name,
                                SwitchId = dSwitch.GetProperty("uuid") as string,
                                Reference = net.obj.Value
                            };
                        }
                    }
                }
                else
                {
                    _logger.LogError($"Unexpected type for Network {name}: {net.obj.type}");
                    continue;
                }

                if (network != null)
                {
                    foreach (var host in net.GetProperty("host") as ManagedObjectReference[])
                    {
                        string hostReference = host.Value;

                        if (!networkCache.ContainsKey(hostReference))
                            networkCache.Add(hostReference, new List<Network>());

                        networkCache[hostReference].Add(network);

                        if (!currentHosts.Contains(hostReference))
                        {
                            currentHosts.Add(hostReference);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error refreshing Network {name} - {net.obj.Value}");
            }
        }

        foreach (var kvp in networkCache)
        {
            NetworkCache.AddOrUpdate(kvp.Key, kvp.Value, (k, v) => (v = kvp.Value));
        }

        foreach (string existingHost in existingHosts.Except(currentHosts))
        {
            if (NetworkCache.TryRemove(existingHost, out List<Network> stale))
            {
                _logger.LogDebug($"removing stale network cache entry for Host {existingHost}");
            }
        }
    }

    private void LoadDatastoreCache(VimClient.ObjectContent[] rawDatastores)
    {
        IEnumerable<string> cachedDatastoreNames = DatastoreCache.Keys;
        List<string> activeDatastoreNames = new List<string>();
        Dictionary<string, Datastore> datastores = new Dictionary<string, Datastore>();
        foreach (var rawDatastore in rawDatastores)
        {
            try
            {
                Datastore datastore = new Datastore
                {
                    Name = rawDatastore.GetProperty("name").ToString(),
                    Reference = rawDatastore.obj,
                    Browser = rawDatastore.GetProperty("browser") as ManagedObjectReference
                };
                DatastoreCache.TryAdd(rawDatastore.GetProperty("name").ToString(), datastore);
                activeDatastoreNames.Add(datastore.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error refreshing Datastore {rawDatastore.obj.Value}");
            }
        }

        // clean cache of non-active datastores
        foreach (var dsName in cachedDatastoreNames)
        {
            if (!activeDatastoreNames.Contains(dsName))
            {
                _logger.LogDebug($"removing stale datastore cache entry {dsName}");
                DatastoreCache.Remove(dsName, out Datastore stale);
            }
        }
    }

    #endregion
}
