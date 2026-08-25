// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The bulk power endpoints in-process, through the real Startup.
///
/// What these cover that the VsphereService unit tests cannot: that a per-Vm reason produced deep in a
/// hypervisor service arrives in the response body under the right key, that each gate in the handler
/// reports one Vm rather than failing the request, and that a rejected Vm still leaves the rest of a
/// multi-select submitted. That contract exists because the UI powers on whatever the user has
/// selected, and a selection routinely includes a machine that is already on.
/// </summary>
public class BulkPowerOperationEndpointTests : IClassFixture<VmApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly VmApiFactory _factory;
    private readonly HttpClient _client;

    public BulkPowerOperationEndpointTests(VmApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();

        // The fixture, and so its substitutes, are shared across the class.
        _factory.Vsphere.ClearSubstitute();
        _factory.Proxmox.ClearSubstitute();
        _factory.PlayerApi.ClearSubstitute();
        _factory.VsphereTasks.ClearSubstitute();
        _factory.AllowEverything();
    }

    private async Task<BulkPowerOperation.Response> Post(string action, params Guid[] ids)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/vms/actions/{action}", new { Ids = ids }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<BulkPowerOperation.Response>(
            JsonOptions, TestContext.Current.CancellationToken);
    }

    private void VsphereReturns(PowerOperation operation, Dictionary<Guid, string> results) =>
        _factory.Vsphere.BulkPowerOperation(Arg.Any<Guid[]>(), operation).Returns(results);

    /// <summary>
    /// The reason this layer exists. One Vm vCenter refuses has to come back as one entry in Errors
    /// while its neighbours are still submitted - the vSphere result dictionary used to be discarded
    /// here, so the caller was told everything went fine.
    /// </summary>
    [Fact]
    public async Task PowerOn_ReportsTheVmVcenterRejectedAndSubmitsTheRest()
    {
        const string reason = "The attempted operation cannot be performed in the current state (Powered on).";

        var accepted = VmApiFactory.VsphereVm();
        var rejected = VmApiFactory.VsphereVm();
        await _factory.SeedAsync(accepted, rejected);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string>
        {
            [accepted.Id] = string.Empty,
            [rejected.Id] = reason
        });

        var result = await Post("power-on", accepted.Id, rejected.Id);

        Assert.Equal(reason, result.Errors.For(rejected.Id));
        Assert.Null(result.Errors.For(accepted.Id));

        // Accepted means "passed the gates and was handed to the hypervisor", which both were.
        Assert.Equal([accepted.Id, rejected.Id], result.Accepted);
    }

    // An empty string is the services' word for "submitted"; it must not reach the client as an error.
    [Fact]
    public async Task PowerOn_ReportsNoErrorsWhenEveryVmWasSubmitted()
    {
        var first = VmApiFactory.VsphereVm();
        var second = VmApiFactory.VsphereVm();
        await _factory.SeedAsync(first, second);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string>
        {
            [first.Id] = string.Empty,
            [second.Id] = string.Empty
        });

        var result = await Post("power-on", first.Id, second.Id);

        Assert.Empty(result.Errors);
        Assert.Equal([first.Id, second.Id], result.Accepted);
    }

    // The handler's own gates report per Vm too, and short-circuit before the hypervisor is called.
    [Fact]
    public async Task PowerOn_ReportsAnUnknownIdWithoutCallingVsphere()
    {
        var known = VmApiFactory.VsphereVm();
        var unknown = Guid.NewGuid();
        await _factory.SeedAsync(known);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [known.Id] = string.Empty });

        var result = await Post("power-on", known.Id, unknown);

        Assert.Equal("Virtual Machine Not Found", result.Errors.For(unknown));
        Assert.Equal([known.Id], result.Accepted);

        await _factory.Vsphere.Received(1).BulkPowerOperation(
            Arg.Is<Guid[]>(x => x.SequenceEqual(new[] { known.Id })), PowerOperation.PowerOn);
    }

    // A Vm the caller cannot see costs one entry in Errors, not the batch. VmService.CanAccessVm throws
    // rather than returning false, so the handler has to catch it - see TryCanAccessVm.
    [Fact]
    public async Task PowerOn_ReportsAnInaccessibleVmWithoutFailingTheRequest()
    {
        var deniedTeam = Guid.NewGuid();
        var denied = VmApiFactory.VsphereVm(teamId: deniedTeam);
        var allowed = VmApiFactory.VsphereVm();
        await _factory.SeedAsync(denied, allowed);

        _factory.PlayerApi
            .CanViewTeams(Arg.Is<IEnumerable<Guid>>(teams => teams.Contains(deniedTeam)), Arg.Any<CancellationToken>())
            .Returns(false);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [allowed.Id] = string.Empty });

        var result = await Post("power-on", denied.Id, allowed.Id);

        Assert.Equal("Unauthorized", result.Errors.For(denied.Id));
        Assert.Equal([allowed.Id], result.Accepted);
    }

    // Revert is gated on a snapshot the database knows about, and again only for the Vm that lacks one.
    [Fact]
    public async Task Revert_ReportsOnlyTheVmWithoutASnapshot()
    {
        var withSnapshot = VmApiFactory.VsphereVm(hasSnapshot: true);
        var withoutSnapshot = VmApiFactory.VsphereVm(hasSnapshot: false);
        await _factory.SeedAsync(withSnapshot, withoutSnapshot);

        VsphereReturns(PowerOperation.Revert, new Dictionary<Guid, string> { [withSnapshot.Id] = string.Empty });

        var result = await Post("revert", withSnapshot.Id, withoutSnapshot.Id);

        Assert.Equal("Virtual Machine does not have a snapshot", result.Errors.For(withoutSnapshot.Id));
        Assert.Equal([withSnapshot.Id], result.Accepted);
    }

    // Unknown is the state the vSphere path refuses outright: with no known power state there is
    // nothing to base a transition on.
    [Fact]
    public async Task PowerOn_ReportsAVsphereVmInAnUnknownPowerState()
    {
        var unknownState = VmApiFactory.VsphereVm(powerState: PowerState.Unknown);
        await _factory.SeedAsync(unknownState);

        var result = await Post("power-on", unknownState.Id);

        Assert.Equal("Unsupported Operation", result.Errors.For(unknownState.Id));
        Assert.Empty(result.Accepted);

        await _factory.Vsphere.DidNotReceive().BulkPowerOperation(Arg.Any<Guid[]>(), Arg.Any<PowerOperation>());
    }

    // Shutdown and reboot are guest-side operations with their own vSphere entry points. Routing them
    // through BulkPowerOperation would cut the power instead of asking the guest.
    [Theory]
    [InlineData("shutdown")]
    [InlineData("reboot")]
    public async Task GuestOperations_DoNotGoThroughTheHardPowerPath(string action)
    {
        var vm = VmApiFactory.VsphereVm(powerState: PowerState.On);
        await _factory.SeedAsync(vm);

        var results = new Dictionary<Guid, string> { [vm.Id] = "No guest tools" };
        _factory.Vsphere.BulkShutdown(Arg.Any<Guid[]>()).Returns(results);
        _factory.Vsphere.BulkReboot(Arg.Any<Guid[]>()).Returns(results);

        var result = await Post(action, vm.Id);

        // Same per-Vm contract on these two paths, which is why it is worth asserting over the wire.
        Assert.Equal("No guest tools", result.Errors.For(vm.Id));
        await _factory.Vsphere.DidNotReceive().BulkPowerOperation(Arg.Any<Guid[]>(), Arg.Any<PowerOperation>());
    }

    /// <summary>
    /// HasPendingTasks is what the UI reads to show a Vm as busy, and TaskService is what clears it. It
    /// is set for accepted Vms before the hypervisor is called, so it is set even for a Vm the
    /// hypervisor then rejects; TaskService clears those on its next pass.
    /// </summary>
    [Fact]
    public async Task PowerOn_MarksEveryAcceptedVmAsPending()
    {
        var accepted = VmApiFactory.VsphereVm();
        var rejected = VmApiFactory.VsphereVm();
        var gated = VmApiFactory.VsphereVm(powerState: PowerState.Unknown);
        await _factory.SeedAsync(accepted, rejected, gated);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string>
        {
            [accepted.Id] = string.Empty,
            [rejected.Id] = "Rejected"
        });

        await Post("power-on", accepted.Id, rejected.Id, gated.Id);

        Assert.True((await _factory.ReadAsync(accepted.Id)).HasPendingTasks);
        Assert.True((await _factory.ReadAsync(rejected.Id)).HasPendingTasks);
        Assert.False((await _factory.ReadAsync(gated.Id)).HasPendingTasks);
    }

    // CheckVsphereTasksBehavior wakes the poller as soon as a command is submitted rather than leaving
    // the UI to wait out a full poll interval. It is a pipeline behavior, so nothing in the handler
    // would notice if the registration were dropped.
    [Fact]
    public async Task PowerOn_WakesTheVsphereTaskPoller()
    {
        var vm = VmApiFactory.VsphereVm();
        await _factory.SeedAsync(vm);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        await Post("power-on", vm.Id);

        _factory.VsphereTasks.Received().CheckTasks();
    }

    // The endpoints are behind the default authorization policy, which the substituted player.api has
    // no say in.
    [Fact]
    public async Task PowerOn_RejectsAnUnauthenticatedRequest()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync(
                "/api/vms/actions/power-on",
                new { Ids = new[] { Guid.NewGuid() } },
                TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

internal static class BulkPowerOperationResponseExtensions
{
    /// <summary>
    /// Response.Errors is keyed by the string form of the Vm id - System.Text.Json could not serialize
    /// Guid keys when that type was written - and spelling the conversion at every assertion buries
    /// what is being asserted. Returns null when the Vm reported no error.
    /// </summary>
    public static string For(this Dictionary<string, string> errors, Guid id) =>
        errors.TryGetValue(id.ToString(), out var reason) ? reason : null;
}
