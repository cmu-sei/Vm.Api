// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The bulk power endpoints in-process, through the real Startup and against real PostgreSQL.
///
/// What these cover that the VsphereService unit tests cannot: that a per-Vm reason produced deep in a
/// hypervisor service arrives in the response body under the right key, that each gate in the handler
/// reports one Vm rather than failing the request, and that a rejected Vm still leaves the rest of a
/// multi-select submitted. That contract exists because the UI powers on whatever the user has
/// selected, and a selection routinely includes a machine that is already on.
/// </summary>
public class BulkPowerOperationEndpointTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // The factory, and so its substitutes, are shared across the class. The database is not.
        Factory.Vsphere.ClearSubstitute();
        Factory.Proxmox.ClearSubstitute();
        Factory.PlayerApi.ClearSubstitute();
        Factory.VsphereTasks.ClearSubstitute();
        Factory.AllowEverything();
    }

    private async Task<BulkPowerOperation.Response> Post(string action, params Guid[] ids)
    {
        var response = await Client.PostAsJsonAsync($"/api/vms/actions/{action}", new { Ids = ids }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<BulkPowerOperation.Response>(JsonOptions, Ct);
    }

    private void VsphereReturns(PowerOperation operation, Dictionary<Guid, string> results) =>
        Factory.Vsphere.BulkPowerOperation(Arg.Any<Guid[]>(), operation).Returns(results);

    private void ProxmoxReturns(PowerOperation operation, Dictionary<Guid, string> results) =>
        Factory.Proxmox.BulkPowerOperation(Arg.Any<Guid[]>(), operation).Returns(results);

    /// <summary>
    /// A Vm of a type neither hypervisor service drives, which the factory has no helper for because no
    /// other test has anything to do with one.
    /// </summary>
    private static Domain.Models.Vm VmOfType(VmType type)
    {
        var id = Guid.NewGuid();

        return new Domain.Models.Vm
        {
            Id = id,
            Name = $"vm-{id}",
            Type = type,
            VmTeams = [new VmTeam(Guid.NewGuid(), id)],
        };
    }

    /// <summary>Re-reads a Vm through a cold change tracker, to assert on what was actually stored.</summary>
    private async Task<Domain.Models.Vm> Stored(Guid id)
    {
        await using var context = NewContext();

        return await context.Vms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
    }

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
        await Seed(accepted, rejected);

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
        await Seed(first, second);

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
        await Seed(known);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [known.Id] = string.Empty });

        var result = await Post("power-on", known.Id, unknown);

        Assert.Equal("Virtual Machine Not Found", result.Errors.For(unknown));
        Assert.Equal([known.Id], result.Accepted);

        await Factory.Vsphere.Received(1).BulkPowerOperation(
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
        await Seed(denied, allowed);

        Factory.PlayerApi
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
        await Seed(withSnapshot, withoutSnapshot);

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
        await Seed(unknownState);

        var result = await Post("power-on", unknownState.Id);

        Assert.Equal("Unsupported Operation", result.Errors.For(unknownState.Id));
        Assert.Empty(result.Accepted);

        await Factory.Vsphere.DidNotReceive().BulkPowerOperation(Arg.Any<Guid[]>(), Arg.Any<PowerOperation>());
    }

    // Shutdown and reboot are guest-side operations with their own vSphere entry points. Routing them
    // through BulkPowerOperation would cut the power instead of asking the guest.
    [Theory]
    [InlineData("shutdown")]
    [InlineData("reboot")]
    public async Task GuestOperations_DoNotGoThroughTheHardPowerPath(string action)
    {
        var vm = VmApiFactory.VsphereVm(powerState: PowerState.On);
        await Seed(vm);

        var results = new Dictionary<Guid, string> { [vm.Id] = "No guest tools" };
        Factory.Vsphere.BulkShutdown(Arg.Any<Guid[]>()).Returns(results);
        Factory.Vsphere.BulkReboot(Arg.Any<Guid[]>()).Returns(results);

        var result = await Post(action, vm.Id);

        // Same per-Vm contract on these two paths, which is why it is worth asserting over the wire.
        Assert.Equal("No guest tools", result.Errors.For(vm.Id));
        await Factory.Vsphere.DidNotReceive().BulkPowerOperation(Arg.Any<Guid[]>(), Arg.Any<PowerOperation>());
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
        await Seed(accepted, rejected, gated);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string>
        {
            [accepted.Id] = string.Empty,
            [rejected.Id] = "Rejected"
        });

        await Post("power-on", accepted.Id, rejected.Id, gated.Id);

        Assert.True((await Stored(accepted.Id)).HasPendingTasks);
        Assert.True((await Stored(rejected.Id)).HasPendingTasks);
        Assert.False((await Stored(gated.Id)).HasPendingTasks);
    }

    // CheckVsphereTasksBehavior wakes the poller as soon as a command is submitted rather than leaving
    // the UI to wait out a full poll interval. It is a pipeline behavior, so nothing in the handler
    // would notice if the registration were dropped.
    [Fact]
    public async Task PowerOn_WakesTheVsphereTaskPoller()
    {
        var vm = VmApiFactory.VsphereVm();
        await Seed(vm);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        await Post("power-on", vm.Id);

        Factory.VsphereTasks.Received().CheckTasks();
    }

    /// <summary>
    /// What the five action bodies do, and all they do: name the operation. Nothing else distinguishes
    /// <c>power-off</c> from <c>power-on</c>, so a route wired to the wrong constant would power machines
    /// the other way and answer 202 either way.
    /// </summary>
    /// <remarks>
    /// The other two of the five are covered by
    /// <see cref="GuestOperations_DoNotGoThroughTheHardPowerPath"/>, which is the point about them: they
    /// do not reach <c>BulkPowerOperation</c> at all.
    /// </remarks>
    [Theory]
    [InlineData("power-on", PowerOperation.PowerOn)]
    [InlineData("power-off", PowerOperation.PowerOff)]
    [InlineData("revert", PowerOperation.Revert)]
    public async Task EachHardPowerRoute_SendsItsOwnOperation(string action, PowerOperation operation)
    {
        // A snapshot so revert passes the gate the other two do not have.
        var vm = VmApiFactory.VsphereVm(hasSnapshot: true);
        await Seed(vm);

        VsphereReturns(operation, new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        var result = await Post(action, vm.Id);

        Assert.Empty(result.Errors);
        Assert.Equal([vm.Id], result.Accepted);

        await Factory.Vsphere.Received(1).BulkPowerOperation(
            Arg.Is<Guid[]>(x => x.SequenceEqual(new[] { vm.Id })), operation);
    }

    #region Proxmox

    /// <summary>
    /// The other hypervisor, which until this test had never been through this handler: a Proxmox Vm is
    /// accepted, dispatched to <c>IProxmoxService</c> and marked pending, and vSphere is not asked
    /// about it.
    /// </summary>
    [Fact]
    public async Task PowerOn_SendsAProxmoxVmToProxmoxAndNotToVsphere()
    {
        var vm = VmApiFactory.ProxmoxVm();
        await Seed(vm);

        ProxmoxReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        var result = await Post("power-on", vm.Id);

        Assert.Empty(result.Errors);
        Assert.Equal([vm.Id], result.Accepted);
        Assert.True((await Stored(vm.Id)).HasPendingTasks);

        await Factory.Proxmox.Received(1).BulkPowerOperation(
            Arg.Is<Guid[]>(x => x.SequenceEqual(new[] { vm.Id })), PowerOperation.PowerOn);
        await Factory.Vsphere.DidNotReceive().BulkPowerOperation(
            Arg.Any<Guid[]>(), Arg.Any<PowerOperation>());
    }

    /// <summary>
    /// A multi-select spanning both hypervisors is split by type, and each service is handed only its own
    /// machines. Handing vCenter a Proxmox id would come back as a batch of "Virtual machine not found".
    /// </summary>
    /// <remarks>
    /// The response's <c>Accepted</c> is in vSphere-then-Proxmox order rather than the order the ids were
    /// submitted in - the handler concatenates two lists - which is why this posts the Proxmox Vm first.
    /// Characterized rather than relied on: the UI reads <c>Errors</c> by id and uses <c>Accepted</c> as a
    /// set.
    /// </remarks>
    [Fact]
    public async Task PowerOn_SplitsAMixedBatchBetweenTheTwoHypervisors()
    {
        var vsphere = VmApiFactory.VsphereVm();
        var proxmox = VmApiFactory.ProxmoxVm();
        await Seed(vsphere, proxmox);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [vsphere.Id] = string.Empty });
        ProxmoxReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [proxmox.Id] = string.Empty });

        var result = await Post("power-on", proxmox.Id, vsphere.Id);

        Assert.Empty(result.Errors);
        Assert.Equal([vsphere.Id, proxmox.Id], result.Accepted);

        await Factory.Vsphere.Received(1).BulkPowerOperation(
            Arg.Is<Guid[]>(x => x.SequenceEqual(new[] { vsphere.Id })), PowerOperation.PowerOn);
        await Factory.Proxmox.Received(1).BulkPowerOperation(
            Arg.Is<Guid[]>(x => x.SequenceEqual(new[] { proxmox.Id })), PowerOperation.PowerOn);
    }

    /// <summary>
    /// Both result dictionaries reach the caller. They are merged one after the other into the same
    /// dictionary as the handler's own gates wrote into, and a merge that dropped either side would report
    /// a rejected machine as submitted - which is the failure this whole class exists for.
    /// </summary>
    [Fact]
    public async Task PowerOn_ReportsAReasonFromEitherHypervisor()
    {
        var vsphere = VmApiFactory.VsphereVm();
        var proxmox = VmApiFactory.ProxmoxVm();
        var unknown = Guid.NewGuid();
        await Seed(vsphere, proxmox);

        VsphereReturns(PowerOperation.PowerOn,
            new Dictionary<Guid, string> { [vsphere.Id] = "vCenter said no" });
        ProxmoxReturns(PowerOperation.PowerOn,
            new Dictionary<Guid, string> { [proxmox.Id] = "pvedaemon said no" });

        var result = await Post("power-on", vsphere.Id, proxmox.Id, unknown);

        Assert.Equal("vCenter said no", result.Errors.For(vsphere.Id));
        Assert.Equal("pvedaemon said no", result.Errors.For(proxmox.Id));
        // And the gate's own entry survives being merged with both of them.
        Assert.Equal("Virtual Machine Not Found", result.Errors.For(unknown));
    }

    /// <summary>
    /// The asymmetry the handler is explicit about: <c>PowerState.Unknown</c> refuses a vSphere Vm but not
    /// a Proxmox one, because a Proxmox Vm reads as Unknown whenever the state poller has not run yet -
    /// including while Proxmox is disabled, and during the first refresh interval after a restart. Gating
    /// on it would refuse a whole batch on the operation meant to fix it.
    /// </summary>
    /// <remarks>
    /// The pair to <see cref="PowerOn_ReportsAVsphereVmInAnUnknownPowerState"/>, and the reason this is
    /// worth a test of its own: the two Vms differ only in <c>Type</c>, so anyone tidying the gate into one
    /// state check for both providers reddens this.
    /// </remarks>
    [Fact]
    public async Task PowerOn_SubmitsAProxmoxVmInAnUnknownPowerState()
    {
        var vm = VmApiFactory.ProxmoxVm(powerState: PowerState.Unknown);
        await Seed(vm);

        ProxmoxReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        var result = await Post("power-on", vm.Id);

        Assert.Empty(result.Errors);
        Assert.Equal([vm.Id], result.Accepted);
    }

    /// <summary>
    /// Revert is refused for a Proxmox Vm outright, and before the snapshot gate rather than by it:
    /// nothing populates <c>HasSnapshot</c> for Proxmox, so every Proxmox Vm would otherwise be reported
    /// as having no snapshot - a true statement about the column and a misleading one about the Vm, which
    /// may well have several.
    /// </summary>
    [Fact]
    public async Task Revert_ForAProxmoxVm_IsUnsupported()
    {
        var vm = VmApiFactory.ProxmoxVm();
        await Seed(vm);

        var result = await Post("revert", vm.Id);

        Assert.Equal("Unsupported Operation", result.Errors.For(vm.Id));
        Assert.Empty(result.Accepted);

        await Factory.Proxmox.DidNotReceive().BulkPowerOperation(
            Arg.Any<Guid[]>(), Arg.Any<PowerOperation>());
    }

    /// <summary>
    /// A Vm of any other type is refused before anything is asked about it. <c>Azure</c> is a type the
    /// enum has and no service in this repository drives; <c>Unknown</c> is what a row written by an
    /// integration that did not set the column reads as.
    /// </summary>
    [Theory]
    [InlineData(VmType.Unknown)]
    [InlineData(VmType.Azure)]
    public async Task PowerOn_ForAVmOfNeitherSupportedType_IsUnsupported(VmType type)
    {
        var vm = VmOfType(type);
        await Seed(vm);

        var result = await Post("power-on", vm.Id);

        Assert.Equal("Unsupported Operation", result.Errors.For(vm.Id));
        Assert.Empty(result.Accepted);
        Assert.False((await Stored(vm.Id)).HasPendingTasks);
    }

    // Both behaviors fire for this command - it implements both marker interfaces - because a batch may
    // have been split across the two hypervisors, and neither poller can tell from a poke whether it has
    // anything to look for.
    [Fact]
    public async Task PowerOn_WakesTheProxmoxTaskPoller()
    {
        var vm = VmApiFactory.ProxmoxVm();
        await Seed(vm);

        ProxmoxReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        await Post("power-on", vm.Id);

        Factory.ProxmoxTasks.Received().CheckTasks();
    }

    #endregion

    #region Permissions

    /// <summary>
    /// The second permission gate, and the one no bulk test had reached: seeing a Vm is not being allowed
    /// to power it. A caller with team visibility but no edit permission gets
    /// <c>"Insufficient Permissions"</c> rather than the <c>"Unauthorized"</c>
    /// <see cref="PowerOn_ReportsAnInaccessibleVmWithoutFailingTheRequest"/> covers, and the two are
    /// different answers to different questions asked of player.api.
    /// </summary>
    [Fact]
    public async Task PowerOn_ReportsInsufficientPermissionsWhenTheCallerCannotEditTheTeam()
    {
        var denied = VmApiFactory.VsphereVm();
        var allowed = VmApiFactory.VsphereVm();
        await Seed(denied, allowed);

        Factory.PlayerApi
            .CanEditTeams(
                Arg.Is<IEnumerable<Guid>>(teams => teams.Contains(denied.VmTeams.Single().TeamId)),
                Arg.Any<CancellationToken>())
            .Returns(false);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [allowed.Id] = string.Empty });

        var result = await Post("power-on", denied.Id, allowed.Id);

        Assert.Equal("Insufficient Permissions", result.Errors.For(denied.Id));
        Assert.Equal([allowed.Id], result.Accepted);
    }

    /// <summary>
    /// Revert asks a different question of player.api than the other four operations do - the
    /// <c>RevertVms</c> view permission rather than team edit - because reverting discards work that
    /// somebody else on the team may be in the middle of.
    /// </summary>
    /// <remarks>
    /// The Vm has a snapshot, so the refusal can only have come from the permission gate; the snapshot
    /// gate sits after it.
    /// </remarks>
    [Fact]
    public async Task Revert_ReportsInsufficientPermissionsWithoutTheRevertPermission()
    {
        var vm = VmApiFactory.VsphereVm(hasSnapshot: true);
        await Seed(vm);

        Factory.PlayerApi
            .Can(
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<AppSystemPermission[]>(),
                Arg.Is<AppViewPermission[]>(x => x != null && x.Contains(AppViewPermission.RevertVms)),
                Arg.Any<AppTeamPermission[]>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await Post("revert", vm.Id);

        Assert.Equal("Insufficient Permissions", result.Errors.For(vm.Id));
        Assert.Empty(result.Accepted);

        await Factory.Vsphere.DidNotReceive().BulkPowerOperation(
            Arg.Any<Guid[]>(), Arg.Any<PowerOperation>());
    }

    /// <summary>
    /// The same denial with the other operation still goes through, which is what makes the test above a
    /// test of the revert branch rather than of any refusal at all: power-on never asks for
    /// <c>RevertVms</c>.
    /// </summary>
    [Fact]
    public async Task PowerOn_IsNotGatedOnTheRevertPermission()
    {
        var vm = VmApiFactory.VsphereVm();
        await Seed(vm);

        Factory.PlayerApi
            .Can(
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<AppSystemPermission[]>(),
                Arg.Is<AppViewPermission[]>(x => x != null && x.Contains(AppViewPermission.RevertVms)),
                Arg.Any<AppTeamPermission[]>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        VsphereReturns(PowerOperation.PowerOn, new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        var result = await Post("power-on", vm.Id);

        Assert.Empty(result.Errors);
        Assert.Equal([vm.Id], result.Accepted);
    }

    #endregion

    // The endpoints are behind the default authorization policy, which the substituted player.api has
    // no say in.
    [Fact]
    public async Task PowerOn_RejectsAnUnauthenticatedRequest()
    {
        var response = await AnonymousClient.PostAsJsonAsync(
            "/api/vms/actions/power-on",
            new { Ids = new[] { Guid.NewGuid() } },
            Ct);

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
