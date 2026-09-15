// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Player.Api.Client;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Vsphere.Extensions;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Features.Vsphere;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Tests.Infrastructure;
using VimClient;
using Xunit;
using DomainMachine = Player.Vm.Api.Domain.Vsphere.Models.VsphereVirtualMachine;
using PlayerTeam = Player.Api.Client.Team;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;
using VsphereMachine = Player.Vm.Api.Features.Vsphere.VsphereVirtualMachine;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The vSphere controller in process, with vCenter on the far side of <c>IVsphereService</c> substituted
/// and everything up to it real: routing, model binding, the MediatR pipeline behaviors, the handlers,
/// <c>VmService.CanAccessVm</c>, <c>NetworkService</c> and <c>IsoService</c> over real PostgreSQL.
///
/// Twenty-one routes, and - as with Proxmox - one gate behind all of them: <c>BaseHandler.GetVm</c> loads
/// the Vm through <c>VmService</c>, which applies the team-visibility and personal-Vm rules, and then asks
/// player.api for whatever permission that particular route needs. So the cross-cutting questions are
/// theories over the whole route table, and the table has a reflection test of its own.
///
/// Two differences from <see cref="ProxmoxEndpointTests"/> shape what is here. There is no provider
/// guard - a Proxmox Vm can be addressed through any of these routes and nothing refuses it, which is
/// characterized rather than corrected below. And the provider instance id a view-network row has to
/// agree with is not configuration but <c>IVsphereService.GetConnectionAddress</c>, so which vCenter a
/// Vm is on is something the substitute decides per test.
///
/// The rest is what only a real request reaches: the values a handler hands vCenter, the precedence
/// between the machine vCenter reports and the row in the database, ISO mount authorization refusing to
/// pass a client's datastore path through, the view-network rows reaching the NIC options, and which
/// routes wake the task poller.
/// </summary>
public class VsphereEndpointTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    /// <summary>
    /// The vCenter the Vm each test seeds is reachable through. Arbitrary, but asserted on: this is the
    /// provider instance id a Proxmox install reads from configuration and vSphere asks the connection
    /// cache for, so a view-network row a test seeds has to agree with it or it belongs to another vCenter.
    /// </summary>
    private const string ConnectionAddress = "vcenter.test";

    private readonly Guid _teamId = Guid.NewGuid();
    private readonly Guid _viewId = Guid.NewGuid();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // The factory, and so its substitutes, are shared across the class. The database is not.
        Factory.Vsphere.ClearSubstitute();
        Factory.PlayerApi.ClearSubstitute();
        Factory.Views.ClearSubstitute();
        Factory.IsoProvider.ClearSubstitute();
        Factory.VsphereTasks.ClearSubstitute();

        Factory.AllowEverything();
        Factory.EnableIsoProvider(VmType.Vsphere);

        // Arrangement rather than subject, all four, and all four are load-bearing:
        //
        //   GetMachineById - GetVsphereVirtualMachine turns a null into a 404, so every route that
        //     returns a machine needs one. GetVsphereVirtualMachine_WhenVCenterDoesNotKnowTheVm_Is404
        //     is the test that owns that branch.
        //   GetConnectionAddress - the provider instance id the view-network lookup filters on. Null
        //     would match no row at all, and silently.
        //   GetViewIdsForTeams - the handlers take .FirstOrDefault() of it.
        //   GetUserTeamIds - the branch NetworkService takes for a caller without view-network access
        //     calls .ToArray() on it.
        Factory.Vsphere.GetMachineById(Arg.Any<Guid>()).Returns(TheMachine());
        Factory.Vsphere.GetConnectionAddress(Arg.Any<Guid>()).Returns(ConnectionAddress);
        Factory.Views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([_viewId]);
        Factory.PlayerApi.GetUserTeamIds(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(x => x.Arg<IEnumerable<Guid>>());
    }

    #region The route table

    /// <summary>
    /// Every route on the controller, as an HTTP method and the suffix under
    /// <c>api/vms/vsphere/{id}</c>. The theories below are all over this, so a route missing from it is a
    /// route with no authorization test at all - which is what
    /// <see cref="TheRouteTable_CoversEveryActionOnTheController"/> is for.
    /// </summary>
    public static TheoryData<string, string> EveryRoute => new()
    {
        { "GET", "" },
        { "GET", "/snapshots" },
        { "GET", "/tools" },
        { "GET", "/isos" },
        { "POST", "/actions/power-on" },
        { "POST", "/actions/power-off" },
        { "POST", "/actions/reboot" },
        { "POST", "/actions/shutdown" },
        { "POST", "/actions/revert" },
        { "POST", "/actions/revert-to-snapshot" },
        { "POST", "/actions/snapshots" },
        { "DELETE", "/actions/snapshots/snap-1" },
        { "POST", "/actions/change-network" },
        { "POST", "/actions/validate-credentials" },
        { "POST", "/actions/upload-file" },
        { "POST", "/actions/file-url" },
        { "POST", "/actions/mount-iso" },
        { "POST", "/actions/set-resolution" },
        { "POST", "/actions/run-guest-process" },
        { "POST", "/actions/run-guest-process-fast" },
        { "POST", "/actions/read-guest-file" },
    };

    /// <summary>
    /// Keeps the table honest. Without this, a route added to the controller silently opts out of every
    /// theory below and nothing goes red.
    /// </summary>
    [Fact]
    public void TheRouteTable_CoversEveryActionOnTheController()
    {
        var actions = typeof(VsphereController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(x => x.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Select(x => x.Name)
            .ToArray();

        Assert.Equal(actions.Length, EveryRoute.Count);
    }

    #endregion

    #region The gate every route shares

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryRoute_RejectsAnUnauthenticatedRequest(string method, string suffix)
    {
        var response = await Send(method, suffix, Guid.NewGuid(), AnonymousClient);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Every route reaches the Vm through <c>VmService.GetAsync</c>, and it is that - not any of the
    /// handlers' own null checks - which refuses an id nothing answers to.
    /// </summary>
    /// <remarks>
    /// Which makes those null checks unreachable. <c>Vsphere/BaseHandler.GetVm</c> and the <c>Get</c>,
    /// <c>GetIsos</c>, <c>GetToolsStatus</c> and <c>ChangeNetwork</c> handlers each follow
    /// <c>GetAsync</c> with <c>if (vm == null) throw new EntityNotFoundException&lt;...&gt;()</c>, but
    /// <c>GetAsync</c> hands the entity to <c>CanAccessVm</c>, which has already thrown by then. The 404
    /// asserted here is <c>CanAccessVm</c>'s, and deleting all five of those checks would not change a
    /// single response.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryRoute_ForAVmThatDoesNotExist_Is404(string method, string suffix)
    {
        var response = await Send(method, suffix, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Vm not found", await Title(response));
    }

    /// <summary>
    /// Team visibility, which <c>GetVm</c> delegates to <c>VmService.CanAccessVm</c> - the same check the
    /// ordinary Vm routes make. Asserted on one route rather than twenty-one because the twenty-one have
    /// already been shown to go through it.
    /// </summary>
    [Fact]
    public async Task WhenTheCallerCannotSeeTheVmsTeams_Is403()
    {
        var vm = await SeedVsphereVm();
        Factory.PlayerApi.CanViewTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await Client.GetAsync(Route(vm.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Insufficient Permissions", await Title(response));
    }

    /// <summary>
    /// The personal-Vm rule, the second half of <c>CanAccessVm</c>: a Vm assigned to a user is that
    /// user's, and reaching it needs a view-wide permission rather than only membership of its team.
    /// </summary>
    [Fact]
    public async Task ForAnotherUsersPersonalVm_WithNoViewWidePermission_Is403()
    {
        var vm = await SeedVsphereVm(userId: Guid.NewGuid());
        DenyEveryPermission();

        var response = await Client.GetAsync(Route(vm.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("This machine belongs to another user", await Title(response));
    }

    /// <summary>
    /// There is no provider guard on these routes, and this is the test that says so: a Proxmox Vm
    /// addressed through the vSphere power route is powered on by <c>IVsphereService</c>, not refused.
    /// </summary>
    /// <remarks>
    /// Deliberately unlike Proxmox, whose <c>BaseHandler</c> answers 403 "This action is only valid for
    /// Proxmox VMs" - see <c>ProxmoxEndpointTests.EveryRoute_ForAVmThatIsNotAProxmoxVm_Is403</c>. It can
    /// be argued either way. vSphere needs no per-Vm connection detail out of the database, so there is
    /// nothing here to dereference, and the real <c>VsphereService</c> keys everything off its own
    /// connection cache: a Vm it has never seen is not found there and the call fails inside the service
    /// rather than at the edge. What that costs is the error - a caller gets whatever the service makes
    /// of an unknown machine instead of a 403 naming the actual mistake. Characterized, not corrected:
    /// adding the guard is a behavior change, and this test is what would catch it being added by
    /// accident.
    /// </remarks>
    [Fact]
    public async Task ForAProxmoxVm_AVsphereRouteIsNotRefused()
    {
        var vm = new VmEntity
        {
            Id = Guid.NewGuid(),
            Name = "a-proxmox-vm",
            Type = VmType.Proxmox,
        };
        vm.VmTeams = [new VmTeam(_teamId, vm.Id)];
        await Seed(vm);

        var response = await Client.PostAsync($"{Route(vm.Id)}/actions/power-on", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.Vsphere.Received(1).PowerOnVm(vm.Id);
    }

    #endregion

    #region The permission each route asks for

    /// <summary>
    /// The routes that change what the Vm is doing or reach into its guest OS, all of which go through
    /// <c>GetVmForEditing</c>. Only the edit permission is denied here, so a 403 can have come from
    /// nowhere else.
    /// </summary>
    [Theory]
    [InlineData("POST", "/actions/power-on")]
    [InlineData("POST", "/actions/power-off")]
    [InlineData("POST", "/actions/reboot")]
    [InlineData("POST", "/actions/shutdown")]
    [InlineData("POST", "/actions/mount-iso")]
    [InlineData("POST", "/actions/set-resolution")]
    [InlineData("POST", "/actions/validate-credentials")]
    [InlineData("POST", "/actions/run-guest-process")]
    [InlineData("POST", "/actions/run-guest-process-fast")]
    public async Task EditingRoutes_WithoutEditPermission_Is403(string method, string suffix)
    {
        var vm = await SeedVsphereVm();
        Deny(AppViewPermission.EditView);

        var response = await Send(method, suffix, vm.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You do not have permission to edit this Vm", await Title(response));
    }

    /// <summary>
    /// Snapshots are all gated on the revert permission - including reading the list, because the names
    /// alone describe what has been done to a machine. The messages differ per route and are asserted
    /// because they are how a client tells which gate refused it.
    /// </summary>
    [Theory]
    [InlineData("GET", "/snapshots", "You do not have permission to view snapshots for this vm.")]
    [InlineData("POST", "/actions/snapshots", "You do not have permission to manage snapshots for this vm.")]
    [InlineData("DELETE", "/actions/snapshots/snap-1", "You do not have permission to manage snapshots for this vm.")]
    [InlineData("POST", "/actions/revert", "You do not have permission to revert this vm.")]
    [InlineData("POST", "/actions/revert-to-snapshot", "You do not have permission to revert this vm.")]
    public async Task SnapshotRoutes_WithoutTheRevertPermission_Is403(
        string method, string suffix, string message)
    {
        var vm = await SeedVsphereVm();
        Deny(AppViewPermission.RevertVms);

        var response = await Send(method, suffix, vm.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(message, await Title(response));
    }

    /// <summary>
    /// Moving a file in or out of a guest has a permission of its own in each direction, and neither is
    /// implied by being able to edit the Vm - which every one of these tests still holds. Note that
    /// <c>file-url</c> counts as reading a file even though it only hands back a URL: what that URL
    /// answers with is the file.
    /// </summary>
    [Theory]
    [InlineData(
        "/actions/read-guest-file",
        AppViewPermission.DownloadVmFiles,
        "You do not have permission to download files from this vm.")]
    [InlineData(
        "/actions/file-url",
        AppViewPermission.DownloadVmFiles,
        "You do not have permission to download files from this vm.")]
    [InlineData(
        "/actions/upload-file",
        AppViewPermission.UploadVmFiles,
        "You do not have permission to upload files to this vm.")]
    public async Task GuestFileRoutes_WithoutTheirOwnPermission_Is403(
        string suffix, AppViewPermission permission, string message)
    {
        var vm = await SeedVsphereVm();
        Deny(permission);

        var response = await Send("POST", suffix, vm.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(message, await Title(response));
    }

    /// <summary>
    /// The other half of the map, and the half a permission check added in the wrong place would break:
    /// reading a Vm with its console ticket, its tools status and its mountable ISOs asks player.api for
    /// no permission at all beyond being able to see the Vm's teams. A team member with nothing else must
    /// still be able to open a console, because that is the whole of what an exercise participant does.
    /// </summary>
    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/tools")]
    [InlineData("GET", "/isos")]
    public async Task ReadRoutes_WithNoPermissionsBeyondTeamVisibility_Are200(string method, string suffix)
    {
        var vm = await SeedVsphereVm();
        DenyEveryPermission();

        var response = await Send(method, suffix, vm.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Power

    [Theory]
    [InlineData("power-on", nameof(IVsphereService.PowerOnVm))]
    [InlineData("power-off", nameof(IVsphereService.PowerOffVm))]
    [InlineData("reboot", nameof(IVsphereService.RebootVm))]
    [InlineData("shutdown", nameof(IVsphereService.ShutdownVm))]
    public async Task PowerRoutes_SubmitTheOperationForTheVmAndReturnTheTaskId(string action, string method)
    {
        var vm = await SeedVsphereVm();
        PowerCommandsReturn("task-2001");

        var response = await Client.PostAsync($"{Route(vm.Id)}/actions/{action}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The vCenter task moref, which is what a caller polls on - as JSON, so quoted.
        Assert.Equal("\"task-2001\"", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(vm.Id, VsphereCall(method)[0]);
    }

    /// <summary>
    /// <c>CheckVsphereTasksBehavior</c> wakes the task poller for any request marked
    /// <c>ICheckVsphereTasksRequest</c>, rather than leaving the Vm to look idle until the next scheduled
    /// poll. Which requests carry the marker is the map worth pinning: the four power operations and both
    /// reverts do, because each starts a vCenter task that changes power state; taking and deleting a
    /// snapshot do not.
    /// </summary>
    [Theory]
    [InlineData("POST", "/actions/power-on", true)]
    [InlineData("POST", "/actions/power-off", true)]
    [InlineData("POST", "/actions/reboot", true)]
    [InlineData("POST", "/actions/shutdown", true)]
    [InlineData("POST", "/actions/revert", true)]
    [InlineData("POST", "/actions/revert-to-snapshot", true)]
    [InlineData("POST", "/actions/snapshots", false)]
    [InlineData("DELETE", "/actions/snapshots/snap-1", false)]
    [InlineData("POST", "/actions/read-guest-file", false)]
    public async Task WhetherARouteWakesTheTaskPoller(string method, string suffix, bool wakes)
    {
        var vm = await SeedVsphereVm();

        var response = await Send(method, suffix, vm.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        if (wakes)
        {
            Factory.VsphereTasks.Received(1).CheckTasks();
        }
        else
        {
            Factory.VsphereTasks.DidNotReceive().CheckTasks();
        }
    }

    #endregion

    #region Reading a Vm

    [Fact]
    public async Task Get_ReturnsTheVmWithTheNetworksTheCallerMaySelect()
    {
        var vm = await SeedVsphereVm();
        await Seed(Network("dvportgroup-51", "red-team-lan"));

        var result = await Get<VsphereMachine>(Route(vm.Id));

        Assert.Equal(vm.Id, result.Id);
        Assert.Equal(vm.Name, result.Name);
        Assert.True(result.CanAccessNicConfiguration);

        // Registered for this View, this provider and this vCenter, so it reaches the NIC options as a
        // selectable network under the name it was registered with.
        var call = VsphereCall(nameof(IVsphereService.GetNicOptions));
        var allowed = (IDictionary<string, string>)call[2];
        Assert.Equal("red-team-lan", Assert.Contains("dvportgroup-51", allowed));

        // Reading a Vm is never managing it: the manage flag is what lets the vSphere layer offer
        // networks outside the allowed list, and this route passes it false unconditionally.
        Assert.False((bool)call[1]);
    }

    /// <summary>
    /// With nothing registered there is nothing to choose between, and the UI reads this flag to decide
    /// whether to offer the control at all.
    /// </summary>
    [Fact]
    public async Task Get_WithNoNetworksRegistered_ReportsTheNicConfigurationAsUnavailable()
    {
        var vm = await SeedVsphereVm();

        var result = await Get<VsphereMachine>(Route(vm.Id));

        Assert.False(result.CanAccessNicConfiguration);
        Assert.Empty((IDictionary<string, string>)VsphereCall(nameof(IVsphereService.GetNicOptions))[2]);
    }

    /// <summary>
    /// A row has to belong to the vCenter this Vm is actually on. One vm.api can serve several vCenters,
    /// and a portgroup moref means nothing outside the one that issued it - so the row is keyed on the
    /// address <c>GetConnectionAddress</c> reports for this Vm, not on any address at all.
    /// </summary>
    [Fact]
    public async Task Get_WithNetworksRegisteredForAnotherVCenter_ReportsNoSelectableNetworks()
    {
        var vm = await SeedVsphereVm();
        var elsewhere = Network("dvportgroup-51", "red-team-lan");
        elsewhere.ProviderInstanceId = "vcenter-other.test";
        await Seed(elsewhere);

        var result = await Get<VsphereMachine>(Route(vm.Id));

        Assert.False(result.CanAccessNicConfiguration);
        Assert.Empty((IDictionary<string, string>)VsphereCall(nameof(IVsphereService.GetNicOptions))[2]);
    }

    /// <summary>
    /// The console ticket, which is the whole point of the route, and the live power state - which comes
    /// from the machine vCenter reports rather than the <c>PowerState</c> column the pollers maintain.
    /// </summary>
    [Fact]
    public async Task Get_ReturnsTheConsoleTicketAndTheStateVCenterReports()
    {
        var vm = await SeedVsphereVm(powerState: PowerState.Off);
        Factory.Vsphere.GetMachineById(vm.Id).Returns(TheMachine(state: "on"));
        Factory.Vsphere.GetConsoleUrl(Arg.Any<DomainMachine>()).Returns("wss://vcenter.test/ticket/52af");
        Factory.Vsphere.GetNicOptions(
                Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<DomainMachine>())
            .Returns(new NicOptions { CurrentNetworks = new() { ["Network adapter 1"] = "dvportgroup-51" } });

        var result = await Get<VsphereMachine>(Route(vm.Id));

        Assert.Equal("wss://vcenter.test/ticket/52af", result.Ticket);
        Assert.Equal("on", result.State);
        Assert.Equal(
            "dvportgroup-51",
            Assert.Contains("Network adapter 1", result.NetworkCards.CurrentNetworks));
    }

    /// <summary>
    /// Two sources disagree about a Vm and the mapping order decides it. The machine from vCenter is
    /// mapped first and the row from the database over the top of it, so <c>HasSnapshot</c> is the stored
    /// flag - the one <c>TaskService</c> maintains - and not what this one call to vCenter said.
    /// </summary>
    [Fact]
    public async Task Get_TakesHasSnapshotFromTheStoredRowRatherThanVCenter()
    {
        var vm = await SeedVsphereVm(hasSnapshot: true);
        Factory.Vsphere.GetMachineById(vm.Id).Returns(TheMachine(hasSnapshot: false));

        Assert.True((await Get<VsphereMachine>(Route(vm.Id))).HasSnapshot);
    }

    /// <summary>
    /// Whose Vm it is, which the UI reads to decide whether to offer a personal Vm's controls. The
    /// caller's own id comes from the token, not from the request.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Get_ReportsWhetherTheCallerOwnsTheVm(bool owned)
    {
        var vm = await SeedVsphereVm(userId: owned ? Factory.UserId : Guid.NewGuid());

        var result = await Get<VsphereMachine>(Route(vm.Id));

        Assert.Equal(owned, result.IsOwner);
        Assert.Equal(vm.UserId, result.UserId);
    }

    /// <summary>
    /// A Vm whose row survives a change on the vCenter side - moved to an unmanaged cluster, or deleted
    /// out from under Player - is a 404 rather than a half-built response with a null ticket.
    /// </summary>
    [Fact]
    public async Task Get_WhenVCenterDoesNotKnowTheVm_Is404()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere.GetMachineById(vm.Id).Returns((DomainMachine)null);

        var response = await Client.GetAsync(Route(vm.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The tools status decides whether the UI offers the guest-agent controls at all, and a generated
    /// client reads it as a name rather than a number - so this is asserted against the raw body, which
    /// <see cref="ApiTestBase.JsonOptions"/> would not notice the converter being removed from.
    /// </summary>
    [Fact]
    public async Task GetToolsStatus_ReturnsWhatVCenterReports_AsAName()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere.GetVmToolsStatus(vm.Id).Returns(VirtualMachineToolsStatus.toolsNotRunning);

        var response = await Client.GetAsync($"{Route(vm.Id)}/tools", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"toolsNotRunning\"", await response.Content.ReadAsStringAsync(Ct));
    }

    #endregion

    #region Snapshots

    [Fact]
    public async Task GetSnapshots_ReturnsWhatVCenterReports()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere.GetSnapshots(vm.Id).Returns(
        [
            new VmSnapshot
            {
                Id = "snapshot-4021",
                Name = "before-patch",
                Description = "taken by the exercise author",
                CreateTime = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                State = "poweredOn",
                IsCurrent = true,
                Depth = 2,
            },
        ]);

        var snapshot = Assert.Single(await Get<VmSnapshot[]>($"{Route(vm.Id)}/snapshots"));

        Assert.Equal("snapshot-4021", snapshot.Id);
        Assert.Equal("before-patch", snapshot.Name);
        Assert.Equal("taken by the exercise author", snapshot.Description);
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc), snapshot.CreateTime);
        Assert.Equal("poweredOn", snapshot.State);
        Assert.True(snapshot.IsCurrent);
        Assert.Equal(2, snapshot.Depth);
    }

    [Fact]
    public async Task CreateSnapshot_PassesTheNameDescriptionAndMemoryFlag()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere
            .CreateSnapshot(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns("task-2002");

        var response = await Post(
            $"{Route(vm.Id)}/actions/snapshots",
            new { SnapshotName = "before-patch", Description = "for the reset", IncludeMemory = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"task-2002\"", await response.Content.ReadAsStringAsync(Ct));

        var call = VsphereCall(nameof(IVsphereService.CreateSnapshot));
        Assert.Equal(vm.Id, call[0]);
        Assert.Equal("before-patch", call[1]);
        Assert.Equal("for the reset", call[2]);
        Assert.Equal(true, call[3]);
    }

    /// <summary>
    /// The snapshot moref is a route segment here, so it arrives through URL decoding - unlike the revert
    /// route below, which takes it from the body.
    /// </summary>
    [Fact]
    public async Task DeleteSnapshot_TakesTheSnapshotFromThePath()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere.DeleteSnapshot(Arg.Any<Guid>(), Arg.Any<string>()).Returns("task-2003");

        var response = await Send("DELETE", "/actions/snapshots/snapshot%204021", vm.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"task-2003\"", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("snapshot 4021", VsphereCall(nameof(IVsphereService.DeleteSnapshot))[1]);
    }

    [Fact]
    public async Task RevertToSnapshot_TakesTheSnapshotFromTheBody()
    {
        var vm = await SeedVsphereVm();

        var response = await Post(
            $"{Route(vm.Id)}/actions/revert-to-snapshot", new { SnapshotId = "snapshot-4021" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.Vsphere.Received(1).RevertToSnapshot(vm.Id, "snapshot-4021");

        // The revert routes answer with no body at all - the work is a vCenter task the caller learns
        // about from the poller, not from a return value.
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The bare revert route goes to whatever the current snapshot is, which is what an exercise reset
    /// button is wired to.
    /// </summary>
    [Fact]
    public async Task Revert_RevertsToTheCurrentSnapshot()
    {
        var vm = await SeedVsphereVm();

        var response = await Client.PostAsync($"{Route(vm.Id)}/actions/revert", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Factory.Vsphere.Received(1).RevertToCurrentSnapshot(vm.Id);
        await Factory.Vsphere.DidNotReceive().RevertToSnapshot(Arg.Any<Guid>(), Arg.Any<string>());
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
    }

    #endregion

    #region Guest agent

    [Fact]
    public async Task RunGuestProcess_ReturnsTheGuestResultAndPassesEveryArgumentThrough()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere
            .RunGuestProcess(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(new GuestProcessResult { Output = "uid=0(root)", ExitCode = 0, Success = true });

        var response = await Post(
            $"{Route(vm.Id)}/actions/run-guest-process",
            new
            {
                Username = "administrator",
                Password = "hunter2",
                ProgramPath = "/bin/sh",
                Arguments = "-c id",
                WorkingDirectory = "/root",
                TimeoutSeconds = 12,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GuestProcessResult>(JsonOptions, Ct);
        Assert.Equal("uid=0(root)", result.Output);
        Assert.True(result.Success);

        var call = VsphereCall(nameof(IVsphereService.RunGuestProcess));
        Assert.Equal(vm.Id, call[0]);
        Assert.Equal("administrator", call[1]);
        Assert.Equal("hunter2", call[2]);
        Assert.Equal("/bin/sh", call[3]);
        Assert.Equal("-c id", call[4]);

        // Honoured here, unlike the Proxmox guest-process routes, whose command binds a working
        // directory that nothing then reads.
        Assert.Equal("/root", call[5]);
        Assert.Equal(TimeSpan.FromSeconds(12), call[6]);
    }

    /// <summary>
    /// No timeout in the request means the configured default, not "wait forever" - a guest process that
    /// never exits would otherwise hold a request thread and a guest-operations slot indefinitely.
    /// </summary>
    [Fact]
    public async Task RunGuestProcess_WithNoTimeout_UsesTheConfiguredDefault()
    {
        var vm = await SeedVsphereVm();
        var configured = Factory.Services.GetRequiredService<IOptions<VsphereOptions>>()
            .Value.GuestProcessDefaultTimeoutSeconds;

        // Otherwise this passes just as well against a host that read no configuration at all.
        Assert.NotEqual(0, configured);

        var response = await Post(
            $"{Route(vm.Id)}/actions/run-guest-process", new { ProgramPath = "/bin/true" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            TimeSpan.FromSeconds(configured),
            VsphereCall(nameof(IVsphereService.RunGuestProcess))[6]);
    }

    /// <summary>
    /// The fast variant answers with the guest process pid rather than the process output, which is what
    /// a caller watches the guest for afterwards. It takes no timeout at all - there is nothing to wait
    /// for.
    /// </summary>
    [Fact]
    public async Task RunGuestProcessFast_ReturnsThePid()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere
            .RunGuestProcessFast(vm.Id, "administrator", "hunter2", "/bin/sh", "-c reboot", "/root")
            .Returns(4242L);

        var response = await Post(
            $"{Route(vm.Id)}/actions/run-guest-process-fast",
            new
            {
                Username = "administrator",
                Password = "hunter2",
                ProgramPath = "/bin/sh",
                Arguments = "-c reboot",
                WorkingDirectory = "/root",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("4242", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The guest path is passed to vCenter's guest operations exactly as submitted.
    /// </summary>
    /// <remarks>
    /// Including one that looks like traversal, which is deliberate rather than an oversight: guest
    /// operations run as the credentials the caller supplied inside the guest, so the guest's own file
    /// permissions are the boundary and there is nothing here for a relative path to escape. The
    /// permission is the whole of the gate, which is why the 403 case above is the test that matters.
    /// </remarks>
    [Theory]
    [InlineData("/etc/hosts")]
    [InlineData("../../etc/shadow")]
    public async Task ReadGuestFile_PassesThePathThroughUnchanged(string path)
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere.ReadGuestFile(vm.Id, "administrator", "hunter2", path)
            .Returns("127.0.0.1 localhost");

        var response = await Post(
            $"{Route(vm.Id)}/actions/read-guest-file",
            new { Username = "administrator", Password = "hunter2", GuestFilePath = path });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"127.0.0.1 localhost\"", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(path, VsphereCall(nameof(IVsphereService.ReadGuestFile))[3]);
    }

    /// <summary>
    /// The download route hands back a URL and the filename to save it under, taken from the guest path.
    /// </summary>
    /// <remarks>
    /// Taken with <c>Path.GetFileName</c>, which splits on the separators of the machine vm.api runs on -
    /// not the guest's. So a Windows guest path comes back whole, backslashes and drive letter included,
    /// because on Linux none of that is a separator. Pinned as observed behavior: a client saving to that
    /// name gets one oddly-named file rather than a wrong one, and the URL - the part that matters - is
    /// correct either way.
    /// </remarks>
    [Theory]
    [InlineData("/var/log/syslog.1", "syslog.1")]
    [InlineData("C:\\temp\\report.txt", "C:\\temp\\report.txt")]
    public async Task GetFileUrl_ReturnsTheUrlAndTheFileNameTakenFromThePath(string path, string fileName)
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere.GetVmFileUrl(vm.Id, "administrator", "hunter2", path)
            .Returns("https://vcenter.test/guestFile?id=17");

        var response = await Post(
            $"{Route(vm.Id)}/actions/file-url",
            new { Username = "administrator", Password = "hunter2", FilePath = path });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GetVmFileUrl.Response>(JsonOptions, Ct);
        Assert.Equal("https://vcenter.test/guestFile?id=17", result.Url);
        Assert.Equal(fileName, result.FileName);
    }

    /// <summary>
    /// Every file in the form is written, each under the submitted path.
    /// </summary>
    /// <remarks>
    /// Concatenated, not joined: the path and the filename are pasted together, so a caller that omits
    /// the trailing separator writes <c>/tmpa.txt</c> and nothing here corrects it. The same as the
    /// Proxmox route does it, and pinned the same way - if either is ever fixed, this is the test that
    /// fails.
    /// </remarks>
    [Fact]
    public async Task UploadFile_WritesEveryFileInTheFormUnderTheSubmittedPath()
    {
        var vm = await SeedVsphereVm();

        var form = new MultipartFormDataContent
        {
            { new StringContent("/tmp/"), "filePath" },
            { new StringContent("administrator"), "username" },
            { new StringContent("hunter2"), "password" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("one")), "files", "a.txt" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("two")), "files", "b.txt" },
        };

        var response = await Client.PostAsync($"{Route(vm.Id)}/actions/upload-file", form, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"Files were successfully uploaded.\"", await response.Content.ReadAsStringAsync(Ct));

        var calls = VsphereCalls(nameof(IVsphereService.UploadFileToVm));
        Assert.Equal<string>(["/tmp/a.txt", "/tmp/b.txt"], calls.Select(x => (string)x[3]));
        Assert.Equal<string>(["administrator", "administrator"], calls.Select(x => (string)x[1]));
    }

    /// <summary>
    /// A guest that refuses the write is a 400 naming the reason, not a 500: bad credentials and a path
    /// that is not writable are both things the caller can act on, and a 500 would have a client retrying
    /// a request that can never succeed.
    /// </summary>
    [Fact]
    public async Task UploadFile_WhenTheGuestRefuses_Is400NamingTheReason()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere
            .UploadFileToVm(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Stream>())
            .ThrowsAsync(new InvalidOperationException("InvalidGuestLogin"));

        var form = new MultipartFormDataContent
        {
            { new StringContent("/tmp/"), "filePath" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("x")), "files", "a.txt" },
        };

        var response = await Client.PostAsync($"{Route(vm.Id)}/actions/upload-file", form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidGuestLogin", await Title(response));
    }

    /// <summary>
    /// How credentials get checked: by uploading an empty file to the guest and seeing whether it is
    /// refused. There is no separate authenticate call in vCenter's guest operations, so an upload of
    /// nothing is the probe.
    /// </summary>
    [Fact]
    public async Task ValidateCredentials_ProbesTheGuestWithAnEmptyUpload()
    {
        var vm = await SeedVsphereVm();

        var response = await Post(
            $"{Route(vm.Id)}/actions/validate-credentials",
            new { Username = "administrator", Password = "hunter2", FilePath = "C:\\Windows\\Temp\\" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"Credentials Authenticated\"", await response.Content.ReadAsStringAsync(Ct));

        var call = VsphereCall(nameof(IVsphereService.UploadFileToVm));
        Assert.Equal(vm.Id, call[0]);
        Assert.Equal("administrator", call[1]);
        Assert.Equal("hunter2", call[2]);
        Assert.Equal("C:\\Windows\\Temp\\", call[3]);
        Assert.Equal(0, ((Stream)call[4]).Length);
    }

    /// <summary>
    /// The probe above is not expected to succeed - the submitted path is usually a directory - so one
    /// particular refusal counts as a pass: a guest that says the target "is not a file" has authenticated
    /// the credentials in order to say it.
    /// </summary>
    /// <remarks>
    /// Matched on the message text, which is the fragile part: it is whatever vCenter's guest operations
    /// happen to word the error as, so a change on that side turns a valid credential into a 400. Pinned
    /// as observed behavior rather than corrected, because the alternative - matching on a fault type -
    /// is a change to <c>VsphereService</c> rather than to the handler.
    /// </remarks>
    [Fact]
    public async Task ValidateCredentials_WhenTheGuestSaysThePathIsNotAFile_IsStillSuccess()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere
            .UploadFileToVm(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Stream>())
            .ThrowsAsync(new InvalidOperationException("C:\\Windows\\Temp\\ is not a file"));

        var response = await Post(
            $"{Route(vm.Id)}/actions/validate-credentials",
            new { Username = "administrator", Password = "hunter2", FilePath = "C:\\Windows\\Temp\\" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"Credentials Authenticated\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ValidateCredentials_WithCredentialsTheGuestRejects_Is400NamingTheReason()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere
            .UploadFileToVm(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Stream>())
            .ThrowsAsync(new InvalidOperationException("InvalidGuestLogin"));

        var response = await Post(
            $"{Route(vm.Id)}/actions/validate-credentials",
            new { Username = "administrator", Password = "wrong", FilePath = "C:\\Windows\\Temp\\" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidGuestLogin", await Title(response));
    }

    /// <summary>
    /// Width and height, in that order and not the other way round - the two are the same type, so
    /// nothing but a test tells them apart.
    /// </summary>
    [Fact]
    public async Task SetResolution_PassesTheWidthAndHeightInOrder()
    {
        var vm = await SeedVsphereVm();
        Factory.Vsphere.SetResolution(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns("task-2004");

        var response = await Post(
            $"{Route(vm.Id)}/actions/set-resolution", new { Width = 1920, Height = 1080 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"task-2004\"", await response.Content.ReadAsStringAsync(Ct));

        var call = VsphereCall(nameof(IVsphereService.SetResolution));
        Assert.Equal(vm.Id, call[0]);
        Assert.Equal(1920, call[1]);
        Assert.Equal(1080, call[2]);
    }

    #endregion

    #region ISOs

    /// <summary>
    /// The picker is built from the storage this Vm's host can reach, not from everything the provider
    /// holds: the mount values come straight back to <c>mount-iso</c>, so an ISO on a datastore this host
    /// cannot see would be offered and then fail to mount.
    /// </summary>
    [Fact]
    public async Task GetIsos_ListsFromTheStorageThisVmCanReach()
    {
        var vm = await SeedVsphereVm();
        IsoScopeIsResolvable();
        Factory.IsoProvider.ListForVmAsync(vm.Id, _viewId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<IsoListingEntry>>
            {
                [_viewId] = [new IsoListingEntry("shared.iso", $"[iso-ds] player/{_viewId}/{_viewId}/shared.iso")],
                [_teamId] = [new IsoListingEntry("team.iso", $"[iso-ds] player/{_viewId}/{_teamId}/team.iso")],
            });

        var result = Assert.Single(await Get<MountableIsoResult[]>($"{Route(vm.Id)}/isos"));

        Assert.Equal(_viewId, result.ViewId);
        Assert.Equal("view-1", result.ViewName);
        Assert.Equal<string>(["shared.iso"], result.Isos.Select(x => x.Filename));

        var team = Assert.Single(result.TeamIsoResults);
        Assert.Equal(_teamId, team.TeamId);
        Assert.Equal<string>(
            [$"[iso-ds] player/{_viewId}/{_teamId}/team.iso"], team.Isos.Select(x => x.MountValue));

        await Factory.IsoProvider.DidNotReceive().ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A Vm whose teams player.api places in no View has no ISO scope to list, and that is an empty
    /// picker rather than an error.
    /// </summary>
    [Fact]
    public async Task GetIsos_ForAVmInNoView_IsAnEmptyList()
    {
        var vm = await SeedVsphereVm();
        Factory.Views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        Assert.Empty(await Get<MountableIsoResult[]>($"{Route(vm.Id)}/isos"));

        // And the storage is not consulted at all - with no scope to list within, a listing could only
        // be everything the provider holds.
        await Factory.IsoProvider.DidNotReceive()
            .ListForVmAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The submitted path is never what gets mounted. A datastore path can name any file the datastore
    /// will serve - including another View's ISO, or a disk image - so it is decoded to a scope,
    /// authorized against this Vm, and rebuilt; the rebuilt value is what reaches vCenter.
    /// </summary>
    [Fact]
    public async Task MountIso_MountsTheFileTheProviderResolvedRatherThanTheOneSubmitted()
    {
        var vm = await SeedVsphereVm();
        Factory.IsoProvider
            .ResolveMountTargetAsync(vm.Id, "[iso-ds] submitted.iso", Arg.Any<CancellationToken>())
            .Returns(new IsoMountTarget(
                _viewId, _viewId, "boot.iso", $"[iso-ds] player/{_viewId}/{_viewId}/boot.iso"));

        var response = await Post(
            $"{Route(vm.Id)}/actions/mount-iso", new { Iso = "[iso-ds] submitted.iso" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var call = VsphereCall(nameof(IVsphereService.ReconfigureVm));
        Assert.Equal(vm.Id, call[0]);
        Assert.Equal(Feature.iso, call[1]);
        Assert.Equal(string.Empty, call[2]);
        Assert.Equal($"[iso-ds] player/{_viewId}/{_viewId}/boot.iso", call[3]);
    }

    /// <summary>
    /// Nothing the provider will not vouch for is mounted. Which values it vouches for is
    /// <see cref="IsoServiceMountAuthTests"/>; what matters here is that a refusal stops the request
    /// before vCenter is touched.
    /// </summary>
    [Fact]
    public async Task MountIso_WithAFileTheProviderWillNotVouchFor_Is403AndMountsNothing()
    {
        var vm = await SeedVsphereVm();

        var response = await Post(
            $"{Route(vm.Id)}/actions/mount-iso", new { Iso = "[other-ds] somebody-elses.iso" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The specified iso is not available to this Vm", await Title(response));

        await AssertNoReconfigure();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MountIso_WithNoIso_Is400(string iso)
    {
        var vm = await SeedVsphereVm();

        var response = await Post($"{Route(vm.Id)}/actions/mount-iso", new { Iso = iso });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("An iso is required", await Title(response));
    }

    #endregion

    #region Changing a network

    [Fact]
    public async Task ChangeNetwork_ToAnAllowedNetwork_ChangesItAndReturnsTheVm()
    {
        var vm = await SeedVsphereVm();
        await Seed(Network("dvportgroup-51", "red-team-lan"));
        VCenterReportsNetworks("dvportgroup-51");

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network",
            new { Adapter = "Network adapter 1", Network = "dvportgroup-51" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content
            .ReadFromJsonAsync<VsphereMachine>(JsonOptions, Ct)).Id);

        var call = VsphereCall(nameof(IVsphereService.ReconfigureVm));
        Assert.Equal(vm.Id, call[0]);
        Assert.Equal(Feature.net, call[1]);
        Assert.Equal("Network adapter 1", call[2]);
        Assert.Equal("dvportgroup-51", call[3]);
    }

    /// <summary>
    /// No networks registered for the View means no permission to change one - the allowed list is the
    /// permission, so an empty one is a refusal rather than an empty menu.
    /// </summary>
    [Fact]
    public async Task ChangeNetwork_WithNothingRegisteredForTheView_Is403()
    {
        var vm = await SeedVsphereVm();

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network",
            new { Adapter = "Network adapter 1", Network = "dvportgroup-51" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You do not have permission to change networks on this VM", await Title(response));

        await AssertNoReconfigure();
    }

    /// <summary>
    /// The submitted network is checked against the allowed list rather than trusted, which is what stops
    /// a caller naming a portgroup that carries another team's traffic.
    /// </summary>
    [Fact]
    public async Task ChangeNetwork_ToANetworkOutsideTheAllowedList_Is403()
    {
        var vm = await SeedVsphereVm();
        await Seed(Network("dvportgroup-51", "red-team-lan"));
        VCenterReportsNetworks("dvportgroup-51", "dvportgroup-99");

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network",
            new { Adapter = "Network adapter 1", Network = "dvportgroup-99" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The target network is not in your allowed networks list", await Title(response));

        await AssertNoReconfigure();
    }

    /// <summary>
    /// The row has to belong to the vCenter this Vm is on, for the same reason
    /// <see cref="Get_WithNetworksRegisteredForAnotherVCenter_ReportsNoSelectableNetworks"/> gives - and
    /// here the consequence is sharper, because a portgroup moref registered for one vCenter could name
    /// something entirely different on another.
    /// </summary>
    [Fact]
    public async Task ChangeNetwork_ToANetworkRegisteredForAnotherVCenter_Is403()
    {
        var vm = await SeedVsphereVm();
        var elsewhere = Network("dvportgroup-51", "red-team-lan");
        elsewhere.ProviderInstanceId = "vcenter-other.test";
        await Seed(elsewhere);
        VCenterReportsNetworks("dvportgroup-51");

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network",
            new { Adapter = "Network adapter 1", Network = "dvportgroup-51" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You do not have permission to change networks on this VM", await Title(response));

        await AssertNoReconfigure();
    }

    /// <summary>
    /// The registered row is checked against what vCenter currently reports for the Vm, so a portgroup
    /// that has been renamed, deleted or moved since it was registered is refused rather than
    /// reconfigured onto a moref that now means something else.
    /// </summary>
    /// <remarks>
    /// The handler guards this second check with <c>if (storedName != null)</c>, which is always true:
    /// <c>NetworkService</c> builds the allowed list with <c>n.Name ?? ""</c>, so a row with no name
    /// yields an empty string rather than a null. The check the guard was meant to make optional is
    /// therefore mandatory - which is the safer of the two readings, and is what this test pins.
    /// </remarks>
    [Fact]
    public async Task ChangeNetwork_WhenVCenterDoesNotReportTheRegisteredNetwork_Is403()
    {
        var vm = await SeedVsphereVm();
        await Seed(Network("dvportgroup-51", "red-team-lan"));
        VCenterReportsNetworks("dvportgroup-77");

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network",
            new { Adapter = "Network adapter 1", Network = "dvportgroup-51" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "Network name mismatch — the registered network may have been renamed or misconfigured",
            await Title(response));

        await AssertNoReconfigure();
    }

    /// <summary>
    /// A request naming no network is a 500, not a 400.
    /// </summary>
    /// <remarks>
    /// There is no validation on this route: the null goes straight into
    /// <c>AllowedNetworks.ContainsKey</c>, which throws <c>ArgumentNullException</c>, and the exception
    /// middleware has no mapping for that. The Proxmox route answers the same request with 400 "An
    /// adapter and target network are required" - see
    /// <c>ProxmoxEndpointTests.ChangeNetwork_WithoutBothAnAdapterAndANetwork_Is400</c>. Characterized
    /// rather than fixed, and worth knowing about: it is reachable by any caller who can change a
    /// network, it logs at error level as an unhandled exception, and nothing is reconfigured either way.
    /// </remarks>
    [Fact]
    public async Task ChangeNetwork_WithNoNetworkNamed_Is500()
    {
        var vm = await SeedVsphereVm();
        await Seed(Network("dvportgroup-51", "red-team-lan"));

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network",
            new { Adapter = "Network adapter 1", Network = (string)null });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        await AssertNoReconfigure();
    }

    #endregion

    #region Helpers

    private static string Route(Guid id) => $"/api/vms/vsphere/{id}";

    /// <summary>
    /// A machine as vCenter reports it. Only the fields the mapping reads are set; the rest of the
    /// vSphere managed-object graph is nothing a handler touches.
    /// </summary>
    private static DomainMachine TheMachine(
        string state = "on",
        bool hasSnapshot = false,
        VirtualMachineToolsStatus toolsStatus = VirtualMachineToolsStatus.toolsOk) =>
        new()
        {
            Name = "as-vcenter-knows-it",
            State = state,
            HasSnapshot = hasSnapshot,
            VmToolsStatus = toolsStatus,
        };

    /// <summary>
    /// Sends a request to one of the controller's routes, with whatever body that route needs to get past
    /// model binding. Binding is not the subject here, but it stands between the test and the handler.
    /// </summary>
    private Task<HttpResponseMessage> Send(
        string method, string suffix, Guid id, HttpClient client = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), $"{Route(id)}{suffix}");

        if (method == "POST")
        {
            request.Content = suffix.EndsWith("upload-file")
                // Read out of Request.Form by hand, so this one needs a real multipart body.
                ? new MultipartFormDataContent
                    { { new ByteArrayContent(Encoding.UTF8.GetBytes("x")), "files", "a.txt" } }
                // Satisfies every [FromBody] parameter; the routes without one ignore it.
                : new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return (client ?? Client).SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> Post(string route, object body) =>
        Client.PostAsJsonAsync(route, body, Ct);

    private async Task<T> Get<T>(string route)
    {
        var response = await Client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    /// <summary>The message the exception middleware put in the ProblemDetails body.</summary>
    private async Task<string> Title(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct))
            .RootElement.GetProperty("title").GetString();

    /// <summary>A vSphere Vm on this test's team, saved.</summary>
    private async Task<VmEntity> SeedVsphereVm(
        Guid? userId = null,
        PowerState powerState = PowerState.On,
        bool hasSnapshot = false)
    {
        var id = Guid.NewGuid();

        var vm = new VmEntity
        {
            Id = id,
            Name = $"vsphere-{id}",
            Type = VmType.Vsphere,
            UserId = userId,
            PowerState = powerState,
            HasSnapshot = hasSnapshot,
            VmTeams = [new VmTeam(_teamId, id)],
        };

        await Seed(vm);

        return vm;
    }

    /// <summary>
    /// A network registered for this test's View, on the vCenter this test's Vm is reachable through, and
    /// shared with this test's team - which is what makes it selectable for a caller who holds no
    /// view-wide network permission.
    /// </summary>
    private ViewNetwork Network(string networkId, string name = null) =>
        new()
        {
            ViewId = _viewId,
            ProviderType = VmType.Vsphere,
            ProviderInstanceId = ConnectionAddress,
            NetworkId = networkId,
            Name = name ?? networkId,
            TeamIds = [_teamId],
        };

    /// <summary>
    /// What vCenter currently reports as the Vm's reachable networks, which <c>ChangeNetwork</c> checks a
    /// registered row against. Unstubbed this answers null and the real code dereferences it, so any test
    /// reaching that check has to say something here.
    /// </summary>
    private void VCenterReportsNetworks(params string[] networkIds) =>
        Factory.Vsphere
            .GetVmNetworks(
                Arg.Any<DomainMachine>(), Arg.Any<bool>(), Arg.Any<Dictionary<string, string>>())
            .Returns(networkIds.ToDictionary(x => x, x => x));

    /// <summary>
    /// Wires the View resolution the ISO listing walks: the Vm's team sits in this test's View, the
    /// caller is a member of it, and player.api can name both.
    /// </summary>
    private void IsoScopeIsResolvable()
    {
        Factory.Views.GetViewIdForTeam(_teamId, Arg.Any<CancellationToken>()).Returns(_viewId);
        Factory.PlayerApi.GetTeamsByViewIdAsync(_viewId, Arg.Any<CancellationToken>())
            .Returns(new[] { new PlayerTeam { Id = _teamId, Name = "team-1", ViewId = _viewId } });
        Factory.PlayerApi.GetViewByIdAsync(_viewId, Arg.Any<CancellationToken>())
            .Returns(new View { Id = _viewId, Name = "view-1" });
    }

    private void PowerCommandsReturn(string taskId)
    {
        Factory.Vsphere.PowerOnVm(Arg.Any<Guid>()).Returns(taskId);
        Factory.Vsphere.PowerOffVm(Arg.Any<Guid>()).Returns(taskId);
        Factory.Vsphere.RebootVm(Arg.Any<Guid>()).Returns(taskId);
        Factory.Vsphere.ShutdownVm(Arg.Any<Guid>()).Returns(taskId);
    }

    /// <summary>
    /// Denies exactly one view permission at player.api, leaving every other answer as
    /// <see cref="VmApiFactory.AllowEverything"/> left it. The narrower arrangement wins for the calls it
    /// matches, so a 403 after this can only have come from the gate that asked for this permission.
    /// </summary>
    private void Deny(AppViewPermission permission) =>
        Factory.PlayerApi.Can(
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<IEnumerable<Guid>>(),
                Arg.Any<AppSystemPermission[]>(),
                Arg.Is<AppViewPermission[]>(x => x != null && x.Contains(permission)),
                Arg.Any<AppTeamPermission[]>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

    /// <summary>
    /// Refuses every permission question, leaving only team visibility - a plain participant on the Vm's
    /// team and nothing more.
    /// </summary>
    private void DenyEveryPermission() =>
        Factory.PlayerApi.Can(default, default, default, default, default, Ct).ReturnsForAnyArgs(false);

    /// <summary>The arguments of the single call to the named <c>IVsphereService</c> method.</summary>
    private object[] VsphereCall(string methodName) => Assert.Single(VsphereCalls(methodName));

    /// <summary>The arguments of each call to the named <c>IVsphereService</c> method, in order.</summary>
    private object[][] VsphereCalls(string methodName) =>
        [.. Factory.Vsphere.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == methodName)
            .Select(x => x.GetArguments())];

    /// <summary>
    /// One method covers both the ISO and the network routes because vSphere reconfigures a Vm through
    /// one call either way - the <c>Feature</c> argument is what says which.
    /// </summary>
    private Task AssertNoReconfigure() =>
        Factory.Vsphere.DidNotReceive().ReconfigureVm(
            Arg.Any<Guid>(), Arg.Any<Feature>(), Arg.Any<string>(), Arg.Any<string>());

    #endregion
}
