// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading.Tasks;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

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
    private readonly HubHarness _harness = new(Guid.NewGuid());

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

        Assert.Equal([vmId.ToString()], _harness.Added);
    }

    [Fact]
    public async Task Join_AddsTheCallersOwnConnection()
    {
        await Hub.Join("anything");

        Assert.Equal(_harness.ConnectionId, Assert.Single(_harness.AddedChanges).ConnectionId);
    }

    [Fact]
    public async Task Leave_RemovesTheSameGroupJoinAdded()
    {
        var vmId = Guid.NewGuid();
        var hub = Hub;

        await hub.Join(vmId.ToString());
        await hub.Leave(vmId.ToString());

        Assert.Equal(_harness.Added, _harness.Removed);
    }

    /// <summary>
    /// There is no authorization here at all: the hub takes a string, never looks at it, and never asks
    /// who the caller is. Any authenticated caller - the hub endpoint requires a token, and that is the
    /// only gate - can subscribe to the task progress of any Vm in the system by naming its id, whether
    /// or not they may see the Vm.
    /// </summary>
    /// <remarks>
    /// This test pins current behaviour and will turn red once that is fixed. The fix is to parse the
    /// string as a Vm id, load the Vm and put it through <c>IVmService.CanAccessVm</c> before joining,
    /// which is what every other route that names a Vm does; a caller who may not see the Vm should get
    /// a <c>HubException</c> as <c>VmHub.JoinUser</c> gives one. What leaks meanwhile is a task's type,
    /// name, state and progress - so the fact that a Vm exists under that id, and what is being done to
    /// it - which is why this is worth a test rather than a comment.
    /// </remarks>
    [Fact]
    public async Task Join_ForAVmTheCallerCannotSee_IsNotRefused()
    {
        var someoneElsesVm = Guid.NewGuid();

        await Hub.Join(someoneElsesVm.ToString());

        Assert.Equal([someoneElsesVm.ToString()], _harness.Added);
    }

    /// <summary>
    /// SignalR group names are compared as ordinal strings, and the pollers build theirs from
    /// <c>Guid.ToString()</c>, which is lower case. A client that upper-cases the id - or sends it in
    /// any of the other formats <c>Guid.Parse</c> accepts - joins a real group that nothing ever
    /// broadcasts to, and sees no progress with no error anywhere to say why.
    /// </summary>
    /// <remarks>
    /// Characterized rather than fixed. Parsing the argument, which is the fix for the authorization
    /// gap above, would normalize this at the same time.
    /// </remarks>
    [Theory]
    [InlineData("N")]
    [InlineData("B")]
    public async Task Join_DoesNotNormalizeTheNameThePollersBroadcastTo(string format)
    {
        var vmId = Guid.NewGuid();

        await Hub.Join(vmId.ToString(format));

        Assert.DoesNotContain(vmId.ToString(), _harness.Added);
    }

    [Fact]
    public async Task Join_WithTheSameNameUpperCased_IsADifferentGroup()
    {
        var vmId = Guid.NewGuid();

        await Hub.Join(vmId.ToString().ToUpperInvariant());

        Assert.DoesNotContain(vmId.ToString(), _harness.Added);
    }
}
