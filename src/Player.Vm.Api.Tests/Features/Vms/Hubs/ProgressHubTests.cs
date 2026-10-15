// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Tests.Support;

namespace Player.Vm.Api.Tests.Features.Vms.Hubs;

/// <summary>
/// ProgressHub is where a client subscribes to a Vm's hypervisor task progress. It has two methods and
/// no dependencies, so what there is to test is entirely the group name: the vSphere and Proxmox task
/// pollers broadcast to <c>vmId.ToString()</c>, and a client that joins any other string is subscribed
/// to a group nothing will ever send to.
/// </summary>
/// <remarks>
/// The name agreement is also proved end to end over a real connection, in
/// <c>HubConnectionTests</c>. What is asserted here is the name itself, which a real client cannot see.
/// </remarks>
public class ProgressHubTests
{
    private const string CallersConnection = "the-callers-connection";

    private readonly HubHarness _harness = new(Guid.NewGuid(), connectionId: CallersConnection);

    private ProgressHub Hub => _harness.Attach(new ProgressHub());

    /// <summary>
    /// The string the caller sends is the group name, unchanged. Both pollers key their notification
    /// dictionaries on <c>vmId.ToString()</c> and send with
    /// <c>_progressHub.Clients.Group(vmTasks.Key)</c>, so any prefixing or trimming here would silently
    /// stop every progress notification reaching every client.
    /// </summary>
    [Fact]
    public async Task Join_UsesTheStringItWasGivenAsTheGroupName()
    {
        var vmId = Guid.NewGuid();

        await Hub.Join(vmId.ToString());

        Assert.Equal([vmId.ToString()], _harness.JoinedGroups);
    }

    [Fact]
    public async Task Join_AddsTheCallersOwnConnection()
    {
        await Hub.Join("anything");

        Assert.Single(_harness.JoinedGroups);
        await _harness.Groups.Received(1).AddToGroupAsync(CallersConnection, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Leave_RemovesTheSameGroupJoinAdded()
    {
        var vmId = Guid.NewGuid();
        var hub = Hub;

        await hub.Join(vmId.ToString());
        await hub.Leave(vmId.ToString());

        Assert.Equal(_harness.JoinedGroups, _harness.LeftGroups);
    }

    /// <summary><c>Join</c> adds the caller to the group of any Vm id it is given.</summary>
    [Fact]
    public async Task Join_ForAVmTheCallerCannotSee_IsNotRefused()
    {
        var someoneElsesVm = Guid.NewGuid();

        await Hub.Join(someoneElsesVm.ToString());

        Assert.Equal([someoneElsesVm.ToString()], _harness.JoinedGroups);
    }

    /// <summary>An id in another <c>Guid</c> format joins a group named by that string, not by the id the pollers use.</summary>
    // Same case as Join_ForAVmTheCallerCannotSee_IsNotRefused.
    [Theory]
    [InlineData("N")]
    [InlineData("B")]
    public async Task Join_DoesNotNormalizeTheNameThePollersBroadcastTo(string format)
    {
        var vmId = Guid.NewGuid();

        await Hub.Join(vmId.ToString(format));

        Assert.DoesNotContain(vmId.ToString(), _harness.JoinedGroups);
    }

    // Same case as Join_ForAVmTheCallerCannotSee_IsNotRefused.
    [Fact]
    public async Task Join_WithTheSameNameUpperCased_IsADifferentGroup()
    {
        var vmId = Guid.NewGuid();

        await Hub.Join(vmId.ToString().ToUpperInvariant());

        Assert.DoesNotContain(vmId.ToString(), _harness.JoinedGroups);
    }
}
