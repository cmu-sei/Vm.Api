// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Vsphere.Extensions;
using Player.Vm.Api.Domain.Vsphere.Options;
using VimClient;

namespace Player.Vm.Api.Domain.Vsphere.Models;

public class VsphereConnection
{
    private volatile VsphereSession _session;
    private TaskCompletionSource _connected = NewSignal();

    // Guards the session and the signal together, so the signal is complete exactly while there is a
    // session. Readers go without it: Current and WhenConnected read one field each.
    private readonly Lock _gate = new();

    // Counts DisconnectAsync calls, so a login begun before one cannot install its session after it.
    private int _disconnects;

    /// <summary>The current login, or null while there is none. Replaced whole by <see cref="Replace"/>.</summary>
    internal VsphereSession Current => _session;

    /// <summary>
    /// The vSphere SOAP operations, typed as <see cref="IVimClient"/> rather than the concrete
    /// generated client so tests can substitute it. See <see cref="IVimClient"/> for why the
    /// generated VimPortType interface cannot be used here directly.
    /// </summary>
    public IVimClient Client => _session?.Client;

    public ServiceContent Sic => _session?.Sic;
    public UserSession Session => _session?.Session;
    public ManagedObjectReference Props => _session?.Sic?.propertyCollector;
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

    public bool Connected => _session != null;

    public VsphereHost Host;
    public VsphereOptions Options;
    private ILogger _logger;
    private bool _forceReload = false;
    private DateTime? LastCacheUpdate;

    // Creates the client for a new login. Replaceable so tests can drive Connect() without a vCenter.
    internal Func<VsphereHost, IVimClient> ClientFactory = CreateClient;

    // WaitForUpdatesEx holds a call open for up to maxWaitSeconds (30) plus server processing time,
    // which the generated binding's one-minute default does not always cover.
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(120);

    public ConcurrentDictionary<string, List<Network>> NetworkCache = new ConcurrentDictionary<string, List<Network>>();
    public ConcurrentDictionary<string, Datastore> DatastoreCache = new ConcurrentDictionary<string, Datastore>();
    public ConcurrentDictionary<string, Guid> VmGuids = new ConcurrentDictionary<string, Guid>();

    /// <summary>
    /// The latest state VsphereMachineWatcher has seen for each machine on this host, including its
    /// moref. <see cref="VmGuids"/> is the reverse index; <see cref="UpsertMachine"/> and
    /// <see cref="RemoveMachine"/> keep the two in step.
    /// </summary>
    public ConcurrentDictionary<Guid, VsphereVirtualMachine> MachineStates = new ConcurrentDictionary<Guid, VsphereVirtualMachine>();

    /// <summary>The watcher's last failure, or null while it is receiving updates.</summary>
    public string WatcherError { get; internal set; }

    public VsphereConnection(VsphereHost host, VsphereOptions options, ILogger logger)
    {
        Host = host;
        Options = options;
        _logger = logger;
    }

    public async Task Load()
    {
        try
        {
            _logger.LogInformation("Starting Connect Loop for {Host} at {Time}", Host.Address, DateTime.UtcNow);

            if (!Host.Enabled)
            {
                _logger.LogInformation("Vsphere disabled, skipping");
            }
            else
            {
                var connected = await Connect();

                if (connected && (LastCacheUpdate == null || (DateTime.UtcNow - LastCacheUpdate.Value).TotalMinutes >= Options.LoadCacheAfterMinutes || _forceReload))
                {
                    try
                    {
                        await LoadCache();
                        LastCacheUpdate = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Exception loading cache for {Host}", Host.Address);
                    }

                    _forceReload = false;
                }

                _logger.LogInformation($"Finished Connect Loop for {Host.Address} at {DateTime.UtcNow} with {MachineStates.Count} Machines");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception encountered in ConnectionService loop");
        }
    }

    #region Connection Handling

    /// <summary>
    /// Completes when a session is next established, or at once while one is. The watcher waits on this
    /// rather than polling while the host is disconnected.
    /// </summary>
    internal Task WhenConnected() => Volatile.Read(ref _connected).Task;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // One session per host, kept for as long as vCenter honours it. VsphereMachineWatcher's
    // long-poll keeps it from idling out, so it is replaced only when the probe below says it is gone.
    private async Task<bool> Connect()
    {
        var epoch = Volatile.Read(ref _disconnects);
        var current = _session;

        if (current?.ClientBase?.State == CommunicationState.Faulted)
        {
            _logger.LogDebug($"Connect():  https://{Host.Address}/sdk CommunicationState is Faulted.");
            _ = Replace(null);
            current = null;
        }

        if (current != null)
        {
            try
            {
                if (await HasSession(current.Client, current.Sic))
                {
                    return true;
                }

                _logger.LogWarning("Connect():  session on {Host} is no longer authenticated. Logging in again.", Host.Address);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking vcenter connection. Reconnecting.");
            }
        }

        IVimClient newClient = null;

        try
        {
            _logger.LogDebug($"Connect():  Instantiating client https://{Host.Address}/sdk");
            newClient = ClientFactory(Host);
            var newSic = await ConnectToHost(newClient);
            var session = await ConnectToSession(newClient, newSic);
            return Install(new VsphereSession(newClient, newSic, session), epoch);
        }
        catch (Exception ex)
        {
            (newClient as VimPortClient)?.Abort();

            // no connection: Failed with Object reference not set to an instance of an object
            _logger.LogError(0, ex, $"Connect():  Failed with " + ex.Message);
            _logger.LogError(0, ex, $"Connect():  User: " + Host.Username);
            _ = Replace(null);
            return false;
        }
    }

    // RetrieveServiceContent answers without a session, so it cannot tell a live session from an
    // expired one. SessionManager.currentSession is null, or the call faults, once the session is gone.
    private static async Task<bool> HasSession(IVimClient client, ServiceContent sic)
    {
        var response = await client.RetrievePropertiesAsync(sic.propertyCollector, [
            new PropertyFilterSpec
            {
                propSet = [new PropertySpec { type = "SessionManager", pathSet = ["currentSession"] }],
                objectSet = [new ObjectSpec { obj = sic.sessionManager }]
            }
        ]);

        return response?.returnval?.FirstOrDefault()?.GetProperty("currentSession") is UserSession;
    }

    private static IVimClient CreateClient(VsphereHost host)
    {
        var client = new VimPortClient(VimPortTypeClient.EndpointConfiguration.VimPort, $"https://{host.Address}/sdk");
        client.Endpoint.Binding.SendTimeout = SendTimeout;
        return client;
    }

    /// <summary>
    /// Makes <paramref name="next"/> the current session, or drops the session when it is null, and
    /// retires the one it replaces in the background. Returns that retirement, which callers other
    /// than <see cref="DisconnectAsync"/> do not await: an unreachable vCenter would hold the connection
    /// loop for the whole send timeout.
    /// </summary>
    internal Task Replace(VsphereSession next)
    {
        VsphereSession old;

        lock (_gate)
        {
            old = Swap(next);
        }

        return old == null ? Task.CompletedTask : RetireAsync(old);
    }

    // A login that outlived a DisconnectAsync - a Load still running when its host was disabled, removed
    // or shut down - would otherwise install a session nothing logs out. It is logged out instead.
    private bool Install(VsphereSession next, int epoch)
    {
        VsphereSession old;
        bool installed;

        lock (_gate)
        {
            installed = _disconnects == epoch;
            old = installed ? Swap(next) : next;
        }

        if (!installed)
        {
            _logger.LogInformation("Connect():  {Host} was disconnected during the login. Logging the new session out.", Host.Address);
        }

        if (old != null)
        {
            _ = RetireAsync(old);
        }

        return installed;
    }

    // Under _gate. The signal is re-armed before the session goes, so a watcher that finds no session
    // always holds a signal the next login will complete.
    private VsphereSession Swap(VsphereSession next)
    {
        var old = _session;

        if (next != null)
        {
            _session = next;
            _connected.TrySetResult();
        }
        else
        {
            if (_connected.Task.IsCompleted)
            {
                Volatile.Write(ref _connected, NewSignal());
            }

            _session = null;
        }

        return old;
    }

    private async Task RetireAsync(VsphereSession session)
    {
        try
        {
            if (session.Sic != null)
            {
                await session.Client.LogoutAsync(session.Sic.sessionManager);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Logout of replaced session on {Host} failed", Host.Address);
        }

        try
        {
            if (session.ClientBase != null)
            {
                await session.ClientBase.CloseAsync();
            }
        }
        catch
        {
            session.ClientBase.Abort();
        }
    }

    /// <summary>
    /// Drops the current session and logs it out. Called by ConnectionService when the host is disabled
    /// or removed, and when the service stops.
    /// </summary>
    public Task DisconnectAsync()
    {
        _logger.LogInformation("Disconnecting from {Host}", Host.Address);
        VsphereSession old;

        lock (_gate)
        {
            _disconnects++;
            old = Swap(null);
        }

        return old == null ? Task.CompletedTask : RetireAsync(old);
    }

    private async Task<ServiceContent> ConnectToHost(IVimClient client)
    {
        _logger.LogInformation($"Connect():  Connecting to {Host.Address}...");
        var sic = await client.RetrieveServiceContentAsync(new ManagedObjectReference { type = "ServiceInstance", Value = "ServiceInstance" });
        return sic;
    }

    private async Task<UserSession> ConnectToSession(IVimClient client, ServiceContent sic)
    {
        _logger.LogInformation($"Connect():  logging into {Host.Address}...[{Host.Username}]");
        var session = await client.LoginAsync(sic.sessionManager, Host.Username, Host.Password, null);
        _logger.LogInformation($"Connect():  Session created.");
        return session;
    }

    #endregion

    #region Cache Setup

    // Networks and datastores. Machines are kept current by VsphereMachineWatcher instead.
    private async Task LoadCache()
    {
        var session = _session ?? throw new InvalidOperationException($"No session for {Host.Address}");

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
                    type = "Datastore",
                    pathSet = new string[] { "name", "browser" }
                }
        };

        ObjectSpec objectspec = new ObjectSpec
        {
            obj = session.Sic.rootFolder,
            selectSet = new SelectionSpec[] { plan }
        };

        PropertyFilterSpec filter = new PropertyFilterSpec
        {
            propSet = props,
            objectSet = new ObjectSpec[] { objectspec }
        };

        PropertyFilterSpec[] filters = new PropertyFilterSpec[] { filter };

        _logger.LogInformation($"Starting RetrieveProperties at {DateTime.UtcNow}");
        RetrievePropertiesResponse response = await session.Client.RetrievePropertiesAsync(session.Sic.propertyCollector, filters);
        _logger.LogInformation($"Finished RetrieveProperties at {DateTime.UtcNow}");

        _logger.LogInformation($"Starting LoadNetworkCache at {DateTime.UtcNow}");
        LoadNetworkCache(
            response.returnval.FindType("DistributedVirtualSwitch"),
            response.returnval.Where(o => o.obj.type.EndsWith("Network") || o.obj.type.EndsWith("DistributedVirtualPortgroup")).ToArray());
        _logger.LogInformation($"Finished LoadNetworkCache at {DateTime.UtcNow}");

        _logger.LogInformation($"Starting LoadDatastoreCache at {DateTime.UtcNow}");
        LoadDatastoreCache(response.returnval.FindType("Datastore"));
        _logger.LogInformation($"Finished LoadDatastoreCache at {DateTime.UtcNow}");
    }

    /// <summary>
    /// Records a machine's latest state in every cache that maps it. Called only by this host's
    /// VsphereMachineWatcher.
    /// </summary>
    internal void UpsertMachine(VsphereVirtualMachine machine)
    {
        // Every live moref keeps its mapping, even when two share a uuid (a copied VM), so tasks on
        // either still resolve. A moref that goes away is dropped by its leave, or by the next snapshot.
        VmGuids[machine.Reference.Value] = machine.Id;
        MachineStates[machine.Id] = machine;
    }

    /// <summary>
    /// Forgets a machine, but only while it is still mapped to <paramref name="reference"/>, so a stale
    /// moref's removal cannot take out a newer mapping for the same uuid.
    /// </summary>
    internal void RemoveMachine(Guid id, string reference)
    {
        VmGuids.TryRemove(new KeyValuePair<string, Guid>(reference, id));

        if (MachineStates.TryGetValue(id, out var state) && state.Reference.Value == reference)
        {
            MachineStates.TryRemove(new KeyValuePair<Guid, VsphereVirtualMachine>(id, state));
        }
    }

    internal void ClearMachines()
    {
        VmGuids.Clear();
        MachineStates.Clear();
        WatcherError = null;
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
                        SwitchId = null,
                        Reference = net.obj.Value
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
