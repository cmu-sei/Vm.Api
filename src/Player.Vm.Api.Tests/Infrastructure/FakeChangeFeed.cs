// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Player.Vm.Api.Domain.Vsphere.Models;
using VimClient;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// One vCenter's session and property-collector change feed, as a substituted <see cref="IVimClient"/>
/// that answers <c>WaitForUpdatesEx</c> from a script.
/// </summary>
/// <remarks>
/// <para>
/// Each call takes the next scripted step: an <see cref="UpdateSet"/>, a <c>null</c> (the server's
/// "nothing changed in maxWaitSeconds"), or an exception. Once the script is spent the call is left
/// pending, as a real long-poll is, until <c>CancelWaitForUpdates</c> releases it with <c>null</c> or
/// <c>Logout</c> faults it. <see cref="Idle"/> is therefore the barrier a test waits on: the watcher has
/// applied every step and is parked on the next call.
/// </para>
/// <para>
/// It also answers the calls <c>VsphereConnection.Connect()</c> makes, so a connection built with
/// <see cref="VsphereConnection.ClientFactory"/> pointing here logs in without a vCenter. The session probe
/// reports a live session while <see cref="SessionValid"/> is set.
/// </para>
/// </remarks>
public sealed class FakeChangeFeed
{
    private readonly ConcurrentQueue<Func<UpdateSet>> _steps = new();
    private readonly object _lock = new();
    private TaskCompletionSource<UpdateSet> _parked;

    public FakeChangeFeed()
    {
        Client.RetrieveServiceContentAsync(Arg.Any<ManagedObjectReference>()).Returns(ServiceContent());
        Client.LoginAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new UserSession { key = "session" });
        Client.RetrievePropertiesAsync(
                Arg.Any<ManagedObjectReference>(),
                Arg.Is<PropertyFilterSpec[]>(x => x[0].propSet[0].type == "SessionManager"))
            .Returns(_ => SessionValid
                ? new RetrievePropertiesResponse([
                    new ObjectContent { propSet = [new DynamicProperty { name = "currentSession", val = new UserSession() }] }
                ])
                : new RetrievePropertiesResponse([]));
        Client.LogoutAsync(Arg.Any<ManagedObjectReference>()).Returns(_ =>
        {
            // The server drops the session's long-poll with it.
            Release(null, new FaultException<NotAuthenticated>(new NotAuthenticated(), new FaultReason("The session is not authenticated.")));
            return Task.CompletedTask;
        });

        Client.CreateContainerViewAsync(
                Arg.Any<ManagedObjectReference>(), Arg.Any<ManagedObjectReference>(), Arg.Any<string[]>(), Arg.Any<bool>())
            .Returns(new CreateContainerViewResponse { returnval = Mor("ContainerView", "view-1") });
        Client.CreatePropertyCollectorAsync(Arg.Any<ManagedObjectReference>())
            .Returns(Mor("PropertyCollector", "collector-1"));
        Client.CreateFilterAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<PropertyFilterSpec>(), Arg.Any<bool>())
            .Returns(Mor("PropertyFilter", "filter-1"));
        Client.WaitForUpdatesExAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<string>(), Arg.Any<WaitOptions>())
            .Returns(x => Next(x.ArgAt<string>(1)));
        Client.CancelWaitForUpdatesAsync(Arg.Any<ManagedObjectReference>()).Returns(_ =>
        {
            Release(null, null);
            return Task.CompletedTask;
        });
    }

    public IVimClient Client { get; } = Substitute.For<IVimClient>();

    public bool SessionValid { get; set; } = true;

    /// <summary>The version each <c>WaitForUpdatesEx</c> call was made with, in order.</summary>
    public ConcurrentQueue<string> Versions { get; } = new();

    /// <summary>Every step has been taken and a call is pending.</summary>
    public bool Idle
    {
        get
        {
            lock (_lock)
            {
                return _steps.IsEmpty && _parked is { Task.IsCompleted: false };
            }
        }
    }

    public FakeChangeFeed Then(UpdateSet update)
    {
        _steps.Enqueue(() => update);
        return this;
    }

    /// <summary>A step built when the call is made, for a test that has to act at that moment.</summary>
    public FakeChangeFeed Then(Func<UpdateSet> step)
    {
        _steps.Enqueue(step);
        return this;
    }

    public FakeChangeFeed ThenTimeout() => Then((UpdateSet)null);

    public FakeChangeFeed ThenThrow(Exception ex)
    {
        _steps.Enqueue(() => throw ex);
        return this;
    }

    public int ViewsCreated => Client.ReceivedCalls().Count(x => x.GetMethodInfo().Name == nameof(IVimClient.CreateContainerViewAsync));

    private Task<UpdateSet> Next(string version)
    {
        Versions.Enqueue(version);

        lock (_lock)
        {
            if (_steps.TryDequeue(out var step))
            {
                try
                {
                    return Task.FromResult(step());
                }
                catch (Exception ex)
                {
                    return Task.FromException<UpdateSet>(ex);
                }
            }

            _parked = new TaskCompletionSource<UpdateSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _parked.Task;
        }
    }

    private void Release(UpdateSet result, Exception ex)
    {
        lock (_lock)
        {
            if (ex != null)
                _parked?.TrySetException(ex);
            else
                _parked?.TrySetResult(result);
        }
    }

    #region Builders

    public static ManagedObjectReference Mor(string type, string value) => new() { type = type, Value = value };

    public static ServiceContent ServiceContent() => new()
    {
        rootFolder = Mor("Folder", "group-d1"),
        viewManager = Mor("ViewManager", "ViewManager"),
        propertyCollector = Mor("PropertyCollector", "propertyCollector"),
        sessionManager = Mor("SessionManager", "SessionManager")
    };

    /// <summary>One page of updates. <paramref name="truncated"/> means more pages follow.</summary>
    public static UpdateSet Page(string version, params ObjectUpdate[] updates) => Page(version, false, updates);

    public static UpdateSet Page(string version, bool truncated, params ObjectUpdate[] updates) => new()
    {
        version = version,
        truncated = truncated,
        truncatedSpecified = truncated,
        filterSet = [new PropertyFilterUpdate { filter = Mor("PropertyFilter", "filter-1"), objectSet = updates }]
    };

    public static ObjectUpdate Enter(
        string reference,
        Guid uuid,
        VirtualMachinePowerState power = VirtualMachinePowerState.poweredOff,
        string[] ips = null,
        bool snapshot = false) => new()
    {
        kind = ObjectUpdateKind.enter,
        obj = Mor("VirtualMachine", reference),
        changeSet =
        [
            Assign("name", reference),
            Assign("config.uuid", uuid.ToString()),
            Assign("summary.runtime.powerState", power),
            Assign("guest.net", ips == null ? null : new[] { new GuestNicInfo { ipAddress = ips } }),
            Assign("rootSnapshot", snapshot ? new[] { Mor("VirtualMachineSnapshot", "snapshot-1") } : null)
        ]
    };

    public static ObjectUpdate Modify(string reference, params PropertyChange[] changes) => new()
    {
        kind = ObjectUpdateKind.modify,
        obj = Mor("VirtualMachine", reference),
        changeSet = changes
    };

    public static ObjectUpdate Leave(string reference) => new()
    {
        kind = ObjectUpdateKind.leave,
        obj = Mor("VirtualMachine", reference)
    };

    public static PropertyChange Assign(string name, object value) =>
        new() { name = name, op = PropertyChangeOp.assign, val = value };

    public static PropertyChange Power(VirtualMachinePowerState state) => Assign("summary.runtime.powerState", state);

    public static PropertyChange Ips(params string[] ips) =>
        Assign("guest.net", new[] { new GuestNicInfo { ipAddress = ips } });

    #endregion
}
