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
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Player.Api.Client;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Features.Proxmox;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Tests.Support;
using ConsoleResponse = Player.Vm.Api.Features.Proxmox.GetConsole.ProxmoxConsole;
using PlayerTeam = Player.Api.Client.Team;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests.Features.Proxmox;

/// <summary>
/// The Proxmox controller in process, with the cluster on the far side of <c>IProxmoxService</c>
/// substituted and everything up to it real: routing, model binding, the MediatR pipeline behaviors, the
/// handlers, <c>VmService.CanAccessVm</c>, <c>NetworkService</c> and <c>IsoService</c> over real
/// PostgreSQL.
///
/// Seventeen routes share one gate. <c>BaseHandler.GetVm</c> loads the Vm, refuses one that is not a
/// Proxmox Vm, applies the team-visibility and personal-Vm rules, and then asks player.api for whatever
/// permission that particular route needs - so the questions worth answering here are the cross-cutting
/// ones: does every route go through it, and does each one ask for the permission it is supposed to. Both
/// are theories over the whole route table, which is also why the table has a test of its own.
///
/// The rest is what only a real request reaches: the values a handler hands the cluster (a snapshot name
/// out of the path, a guest process timeout defaulted from configuration, a file path concatenated with
/// an uploaded filename), the ISO mount authorization refusing to pass a client's volume id through, the
/// view-network rows reaching the NIC options, and which routes wake the task poller.
/// </summary>
public class ProxmoxEndpointTests(DatabaseFixture fixture, VmAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmAppFactory>
{
    /// <summary>
    /// The PVE vmid and node of the Vm each test seeds. Arbitrary values, but asserted on: what the
    /// handlers hand the cluster is the <c>ProxmoxVmInfo</c> of the Vm that was asked for, and matching
    /// on these is how a test says so.
    /// </summary>
    private const int Vmid = 101;

    private const string Node = "pve-node-1";

    private readonly Guid _teamId = Guid.NewGuid();
    private readonly Guid _viewId = Guid.NewGuid();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // The factory, and so its substitutes, are shared across the class. The database is not.
        Factory.Proxmox.ClearSubstitute();
        Factory.IsoProvider.ClearSubstitute();
        Factory.ProxmoxTasks.ClearSubstitute();


        // Proxmox rather than the default vSphere: IsoService picks the provider by Vm type, and
        // ProxmoxVmNetworkService reports CanMountIso from whether a Proxmox one is enabled.
        Factory.EnableIsoProvider(VmType.Proxmox);

        // Arrangement rather than subject, all three, and all three are load-bearing for any route that
        // builds a response: an unstubbed substitute answers null and the real code on the other side
        // dereferences it.
        //
        //   GetViewIdsForTeams - ProxmoxVmNetworkService.GetPermissions takes .FirstOrDefault() of it.
        //   GetVmConfigSummary - ToResponse reads .CurrentNetworks off it.
        Factory.Views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([_viewId]);

        // Root holds every system permission but the two network ones, so NetworkService takes the branch
        // a caller without view-network access takes: the networks shared with Root's own teams, which is
        // what the network tests are about.
        Factory.Player.PlaceTeam(_teamId, _viewId);
        await Actor().WithId(Root.Id).MemberOf(_teamId).SeedAsync();
        Factory.Player.Caller(Root.Id).System.ExceptWith(NetworkPermissions);
        Factory.Proxmox.GetVmConfigSummary(Arg.Any<ProxmoxVmInfo>(), Arg.Any<CancellationToken>())
            .Returns(new ProxmoxVmConfigSummary(new() { ["net0"] = "vmbr0" }, true));
    }

    #region The route table

    /// <summary>
    /// Every route on the controller, as an HTTP method and the suffix under
    /// <c>api/vms/proxmox/{id}</c>. The theories below are all over this, so a route missing from it is a
    /// route with no authorization test at all - which is what
    /// <see cref="TheRouteTable_CoversEveryActionOnTheController"/> is for.
    /// </summary>
    public static TheoryData<string, string> EveryRoute => new()
    {
        { "GET", "" },
        { "GET", "/console" },
        { "GET", "/isos" },
        { "GET", "/snapshots" },
        { "POST", "/actions/power-on" },
        { "POST", "/actions/power-off" },
        { "POST", "/actions/reboot" },
        { "POST", "/actions/shutdown" },
        { "POST", "/actions/change-network" },
        { "POST", "/actions/mount-iso" },
        { "POST", "/actions/run-guest-process" },
        { "POST", "/actions/run-guest-process-fast" },
        { "POST", "/actions/read-guest-file" },
        { "POST", "/actions/upload-file" },
        { "POST", "/actions/snapshots" },
        { "POST", "/actions/snapshots/snap-1/revert" },
        { "DELETE", "/actions/snapshots/snap-1" },
    };

    /// <summary>
    /// Keeps the table honest. Without this, a route added to the controller silently opts out of every
    /// theory below and nothing fails.
    /// </summary>
    [Fact]
    public void TheRouteTable_CoversEveryActionOnTheController()
    {
        var actions = typeof(ProxmoxController)
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
        var response = await Send(method, suffix, Guid.NewGuid(), Client());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryRoute_ForAVmThatDoesNotExist_Is404(string method, string suffix)
    {
        var response = await Send(method, suffix, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The provider guard, and the reason it is worth asserting seventeen times: these routes reach a
    /// Proxmox cluster with a vmid and a node taken from <c>ProxmoxVmInfo</c>. A vSphere Vm has none, so
    /// a route that skipped the guard would either dereference null or - worse - act on whatever Proxmox
    /// Vm happened to answer to a default.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryRoute_ForAVmThatIsNotAProxmoxVm_Is403(string method, string suffix)
    {
        var vm = TestData.VsphereVm(_teamId);
        await Seed(vm);

        var response = await Send(method, suffix, vm.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("This action is only valid for Proxmox VMs", await Title(response));
    }

    /// <summary>
    /// Vm visibility, which <c>GetVm</c> delegates to <c>VmService.CanAccessVm</c> - the same check the
    /// ordinary Vm routes make. Asserted on one route rather than seventeen because the seventeen have
    /// already been shown to go through <c>GetVm</c>.
    /// </summary>
    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_ViewTeamVms_only_on_another_team()
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamVms, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).GetAsync(Route(vm.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Insufficient Permissions", await Title(response));
    }

    [Fact]
    public async Task GetConsole_is_forbidden_for_a_caller_holding_ViewTeamVms_only_on_another_team()
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamVms, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).GetAsync($"{Route(vm.Id)}/console", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Insufficient Permissions", await Title(response));
        await Factory.Proxmox.DidNotReceive().GetConsole(Arg.Any<ProxmoxVmInfo>());
    }

    [Fact]
    public async Task GetIsos_is_forbidden_for_a_caller_holding_ViewTeamVms_only_on_another_team()
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamVms, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).GetAsync($"{Route(vm.Id)}/isos", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Insufficient Permissions", await Title(response));
    }

    [Fact]
    public async Task ChangeNetwork_is_forbidden_for_a_caller_holding_ViewTeamVms_only_on_another_team()
    {
        var vm = await SeedProxmoxVm();
        await Seed(Network("vmbr9"));
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamVms, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            $"{Route(vm.Id)}/actions/change-network", new { Adapter = "net0", Network = "vmbr9" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Insufficient Permissions", await Title(response));

        await AssertNoNetworkChange();
    }

    /// <summary>
    /// The personal-Vm rule, the second half of <c>CanAccessVm</c>: a Vm assigned to a user is that
    /// user's, and reaching it needs a view-wide permission rather than only membership of its team.
    /// </summary>
    [Fact]
    public async Task ForAnotherUsersPersonalVm_WithNoViewWidePermission_Is403()
    {
        var vm = await SeedProxmoxVm(userId: Guid.NewGuid());
        var response = await (await TeamParticipant()).GetAsync(Route(vm.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("This machine belongs to another user", await Title(response));
    }

    #endregion

    #region The permission each route asks for

    /// <summary>
    /// The routes that change what the Vm is doing, all of which go through <c>GetVmForEditing</c>. Only
    /// the Vm-control permission is denied here, so a 403 can have come from nowhere else.
    /// </summary>
    [Theory]
    [InlineData("POST", "/actions/power-on")]
    [InlineData("POST", "/actions/power-off")]
    [InlineData("POST", "/actions/reboot")]
    [InlineData("POST", "/actions/shutdown")]
    [InlineData("POST", "/actions/mount-iso")]
    [InlineData("POST", "/actions/run-guest-process")]
    [InlineData("POST", "/actions/run-guest-process-fast")]
    public async Task EditingRoutes_WithoutVmControlPermission_Is403(string method, string suffix)
    {
        var vm = await SeedProxmoxVm();
        var response = await Send(method, suffix, vm.Id, await AllBut(AppViewPermission.ControlViewVms));

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
    [InlineData("POST", "/actions/snapshots/snap-1/revert", "You do not have permission to revert this vm.")]
    [InlineData("DELETE", "/actions/snapshots/snap-1", "You do not have permission to manage snapshots for this vm.")]
    public async Task SnapshotRoutes_WithoutTheRevertPermission_Is403(
        string method, string suffix, string message)
    {
        var vm = await SeedProxmoxVm();
        var response = await Send(method, suffix, vm.Id, await AllBut(AppViewPermission.RevertVms));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(message, await Title(response));
    }

    /// <summary>
    /// Moving a file in or out of a guest has a permission of its own in each direction, and neither is
    /// implied by being able to edit the Vm - which every one of these tests still holds.
    /// </summary>
    [Theory]
    [InlineData(
        "/actions/read-guest-file",
        AppViewPermission.DownloadVmFiles,
        "You do not have permission to download files from this vm.")]
    [InlineData(
        "/actions/upload-file",
        AppViewPermission.UploadVmFiles,
        "You do not have permission to upload files to this vm.")]
    public async Task GuestFileRoutes_WithoutTheirOwnPermission_Is403(
        string suffix, AppViewPermission permission, string message)
    {
        var vm = await SeedProxmoxVm();
        var response = await Send("POST", suffix, vm.Id, await AllBut(permission));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(message, await Title(response));
    }

    /// <summary>
    /// The editing routes refuse a caller who controls another team's Vms and may only see this one's.
    /// </summary>
    [Theory]
    [InlineData("POST", "/actions/power-on")]
    [InlineData("POST", "/actions/power-off")]
    [InlineData("POST", "/actions/reboot")]
    [InlineData("POST", "/actions/shutdown")]
    [InlineData("POST", "/actions/mount-iso")]
    [InlineData("POST", "/actions/run-guest-process")]
    [InlineData("POST", "/actions/run-guest-process-fast")]
    public async Task EditingRoutes_are_forbidden_for_a_caller_holding_ControlTeamVms_only_on_another_team(string method, string suffix)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            
                .Holding(AppTeamPermission.ViewTeamVms, _teamId)
                .Holding(AppTeamPermission.ControlTeamVms, Guid.NewGuid())
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You do not have permission to edit this Vm", await Title(response));
    }

    [Theory]
    [InlineData("POST", "/actions/power-on", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/power-off", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/reboot", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/shutdown", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/mount-iso", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/actions/run-guest-process", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/run-guest-process-fast", HttpStatusCode.OK)]
    public async Task EditingRoutes_are_allowed_for_a_caller_holding_exactly_ControlTeamVms_on_the_Vms_team(
        string method, string suffix, HttpStatusCode expected)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppTeamPermission.ControlTeamVms, _teamId)
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        // The empty body passes the gate and nothing else checks permission; mount-iso then wants an iso.
        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// <c>ControlViewVms</c> is carried on the caller's own team in the Vm's View, not on the Vm's team, so
    /// the editing gate reaches it through the View: no team permission on the Vm's team is held.
    /// </summary>
    [Theory]
    [InlineData("POST", "/actions/power-on", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/power-off", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/reboot", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/shutdown", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/mount-iso", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/actions/run-guest-process", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/run-guest-process-fast", HttpStatusCode.OK)]
    public async Task EditingRoutes_are_allowed_for_a_caller_holding_exactly_ControlViewVms_on_the_Vms_view(
        string method, string suffix, HttpStatusCode expected)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppViewPermission.ControlViewVms, _viewId)
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        // The empty body passes the gate and nothing else checks permission; mount-iso then wants an iso.
        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// <c>ViewTeamVms</c> on the Vm's team lets the caller see the Vm, and <c>ControlViewVms</c> in another
    /// View is the very permission the editing gate asks for, held where it does not reach this Vm.
    /// </summary>
    [Theory]
    [InlineData("POST", "/actions/power-on")]
    [InlineData("POST", "/actions/power-off")]
    [InlineData("POST", "/actions/reboot")]
    [InlineData("POST", "/actions/shutdown")]
    [InlineData("POST", "/actions/mount-iso")]
    [InlineData("POST", "/actions/run-guest-process")]
    [InlineData("POST", "/actions/run-guest-process-fast")]
    public async Task EditingRoutes_are_forbidden_for_a_caller_holding_ControlViewVms_only_on_another_view(string method, string suffix)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamVms, _teamId)
            .Holding(AppViewPermission.ControlViewVms, Guid.NewGuid())
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You do not have permission to edit this Vm", await Title(response));
    }

    [Theory]
    [InlineData("POST", "/actions/power-on", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/power-off", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/reboot", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/shutdown", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/mount-iso", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/actions/run-guest-process", HttpStatusCode.OK)]
    [InlineData("POST", "/actions/run-guest-process-fast", HttpStatusCode.OK)]
    public async Task EditingRoutes_are_allowed_for_a_caller_holding_exactly_the_system_ControlVms(
        string method, string suffix, HttpStatusCode expected)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppSystemPermission.ControlVms)
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        // The empty body passes the gate and nothing else checks permission; mount-iso then wants an iso.
        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// The system <c>ViewVms</c> sees every Vm, which is the read the editing routes make first, and
    /// controls none of them.
    /// </summary>
    [Theory]
    [InlineData("POST", "/actions/power-on")]
    [InlineData("POST", "/actions/power-off")]
    [InlineData("POST", "/actions/reboot")]
    [InlineData("POST", "/actions/shutdown")]
    [InlineData("POST", "/actions/mount-iso")]
    [InlineData("POST", "/actions/run-guest-process")]
    [InlineData("POST", "/actions/run-guest-process-fast")]
    public async Task EditingRoutes_are_forbidden_for_a_caller_holding_only_the_system_ViewVms(string method, string suffix)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppSystemPermission.ViewVms)
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You do not have permission to edit this Vm", await Title(response));
    }

    /// <summary>
    /// The snapshot and guest-file routes ask for a View permission through the Vm's team, so the same
    /// permission on another View does not reach them.
    /// </summary>
    [Theory]
    [InlineData("GET", "/snapshots", AppViewPermission.RevertVms, "You do not have permission to view snapshots for this vm.")]
    [InlineData("POST", "/actions/snapshots", AppViewPermission.RevertVms, "You do not have permission to manage snapshots for this vm.")]
    [InlineData("POST", "/actions/snapshots/snap-1/revert", AppViewPermission.RevertVms, "You do not have permission to revert this vm.")]
    [InlineData("DELETE", "/actions/snapshots/snap-1", AppViewPermission.RevertVms, "You do not have permission to manage snapshots for this vm.")]
    [InlineData("POST", "/actions/read-guest-file", AppViewPermission.DownloadVmFiles, "You do not have permission to download files from this vm.")]
    [InlineData("POST", "/actions/upload-file", AppViewPermission.UploadVmFiles, "You do not have permission to upload files to this vm.")]
    public async Task ViewPermissionRoutes_are_forbidden_for_a_caller_holding_their_permission_only_on_another_view(
        string method, string suffix, AppViewPermission permission, string message)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            
                .Holding(AppTeamPermission.ViewTeamVms, _teamId)
                .Holding(permission, Guid.NewGuid())
                .InView(_teamId, _viewId)
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(message, await Title(response));
    }

    [Theory]
    [InlineData("GET", "/snapshots", AppViewPermission.RevertVms, "You do not have permission to view snapshots for this vm.")]
    [InlineData("POST", "/actions/snapshots", AppViewPermission.RevertVms, "You do not have permission to manage snapshots for this vm.")]
    [InlineData("POST", "/actions/snapshots/snap-1/revert", AppViewPermission.RevertVms, "You do not have permission to revert this vm.")]
    [InlineData("DELETE", "/actions/snapshots/snap-1", AppViewPermission.RevertVms, "You do not have permission to manage snapshots for this vm.")]
    [InlineData("POST", "/actions/read-guest-file", AppViewPermission.DownloadVmFiles, "You do not have permission to download files from this vm.")]
    [InlineData("POST", "/actions/upload-file", AppViewPermission.UploadVmFiles, "You do not have permission to upload files to this vm.")]
    public async Task ViewPermissionRoutes_are_allowed_for_a_caller_holding_exactly_their_permission_on_the_Vms_view(
        string method, string suffix, AppViewPermission permission, string message)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            
                .Holding(AppTeamPermission.ViewTeamVms, _teamId)
                .Holding(permission, _viewId)
                .InView(_teamId, _viewId)
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(message, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The snapshot and guest-file routes each have a system permission of the same name as their View
    /// permission, so a system administrator on none of the Vm's teams reaches them without being added
    /// to a team. The system <c>ViewVms</c> alongside it passes the read every route makes first.
    /// </summary>
    [Theory]
    [InlineData("GET", "/snapshots", AppSystemPermission.RevertVms)]
    [InlineData("POST", "/actions/snapshots", AppSystemPermission.RevertVms)]
    [InlineData("POST", "/actions/snapshots/snap-1/revert", AppSystemPermission.RevertVms)]
    [InlineData("DELETE", "/actions/snapshots/snap-1", AppSystemPermission.RevertVms)]
    [InlineData("POST", "/actions/read-guest-file", AppSystemPermission.DownloadVmFiles)]
    [InlineData("POST", "/actions/upload-file", AppSystemPermission.UploadVmFiles)]
    public async Task SnapshotAndGuestFileRoutes_are_allowed_for_a_caller_holding_exactly_their_system_permission_and_ViewVms(
        string method, string suffix, AppSystemPermission permission)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppSystemPermission.ViewVms, permission)
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        Assert.True(response.IsSuccessStatusCode, $"{method} {suffix} was {response.StatusCode}");
    }

    /// <summary>
    /// Controlling every Vm does not imply moving files in or out of them or discarding their state -
    /// each of those is its own system permission, as it is at View level.
    /// </summary>
    [Theory]
    [InlineData("GET", "/snapshots", "You do not have permission to view snapshots for this vm.")]
    [InlineData("POST", "/actions/snapshots", "You do not have permission to manage snapshots for this vm.")]
    [InlineData("POST", "/actions/snapshots/snap-1/revert", "You do not have permission to revert this vm.")]
    [InlineData("DELETE", "/actions/snapshots/snap-1", "You do not have permission to manage snapshots for this vm.")]
    [InlineData("POST", "/actions/read-guest-file", "You do not have permission to download files from this vm.")]
    [InlineData("POST", "/actions/upload-file", "You do not have permission to upload files to this vm.")]
    public async Task SnapshotAndGuestFileRoutes_are_forbidden_for_a_caller_holding_only_the_system_ControlVms(
        string method, string suffix, string message)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor()
            .Holding(AppSystemPermission.ControlVms)
            .SeedAsync();

        var response = await Send(method, suffix, vm.Id, Client(actor));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(message, await Title(response));
    }

    /// <summary>
    /// The other half of the map, and the half a permission check added in the wrong place would break:
    /// reading a Vm, its console and its mountable ISOs asks player.api for no permission at all beyond
    /// being able to see the Vm's teams. A team member with nothing else must still be able to open a
    /// console, because that is the whole of what an exercise participant does.
    /// </summary>
    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/console")]
    [InlineData("GET", "/isos")]
    public async Task ReadRoutes_WithNoPermissionsBeyondTeamVisibility_Are200(string method, string suffix)
    {
        var vm = await SeedProxmoxVm();
        var response = await Send(method, suffix, vm.Id, await TeamParticipant());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The read routes ask CanAccessVm alone, so each Vm read value it accepts reads the Vm, and a caller
    // holding a neighbouring permission, or the View value in another View, is refused there.

    [Theory]
    [InlineData(AppSystemPermission.ViewVms)]
    [InlineData(AppSystemPermission.ControlVms)]
    public async Task Get_is_allowed_for_a_caller_holding_exactly_a_system_Vm_read_permission(AppSystemPermission permission)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor().Holding(permission).SeedAsync();

        var response = await Client(actor).GetAsync(Route(vm.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content.ReadFromJsonAsync<ProxmoxVirtualMachine>(JsonOptions, Ct)).Id);
    }

    [Theory]
    [InlineData(AppViewPermission.ViewViewVms)]
    [InlineData(AppViewPermission.ControlViewVms)]
    public async Task Get_is_allowed_for_a_caller_holding_exactly_a_Vm_read_permission_on_the_Vms_view(AppViewPermission permission)
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor().Holding(permission, _viewId).SeedAsync();

        var response = await Client(actor).GetAsync(Route(vm.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content.ReadFromJsonAsync<ProxmoxVirtualMachine>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task Get_is_allowed_for_a_caller_holding_exactly_ControlTeamVms_on_the_Vms_team()
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor().Holding(AppTeamPermission.ControlTeamVms, _teamId).SeedAsync();

        var response = await Client(actor).GetAsync(Route(vm.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content.ReadFromJsonAsync<ProxmoxVirtualMachine>(JsonOptions, Ct)).Id);
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_only_the_system_ViewMaps()
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor().Holding(AppSystemPermission.ViewMaps).SeedAsync();

        var response = await Client(actor).GetAsync(Route(vm.Id), Ct);

        await AssertStatus(HttpStatusCode.Forbidden, response);
        Assert.Equal("Insufficient Permissions", await Title(response));
    }

    [Fact]
    public async Task Get_is_forbidden_for_a_caller_holding_ViewViewVms_only_in_another_view()
    {
        var vm = await SeedProxmoxVm();
        var otherView = Guid.NewGuid();
        Factory.Player.AddView(otherView);
        var actor = await Actor().Holding(AppViewPermission.ViewViewVms, otherView).SeedAsync();

        var response = await Client(actor).GetAsync(Route(vm.Id), Ct);

        await AssertStatus(HttpStatusCode.Forbidden, response);
        Assert.Equal("Insufficient Permissions", await Title(response));
    }

    #endregion

    #region Power

    [Theory]
    [InlineData("power-on", nameof(IProxmoxService.PowerOnVm))]
    [InlineData("power-off", nameof(IProxmoxService.PowerOffVm))]
    [InlineData("reboot", nameof(IProxmoxService.RebootVm))]
    [InlineData("shutdown", nameof(IProxmoxService.ShutdownVm))]
    public async Task PowerRoutes_SubmitTheOperationForTheVmAndReturnTheTaskId(string action, string method)
    {
        var vm = await SeedProxmoxVm();
        PowerCommandsReturn("UPID:pve-node-1:0001");

        var response = await RootClient.PostAsync($"{Route(vm.Id)}/actions/{action}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The Proxmox task id, which is what a caller polls on - as JSON, so quoted.
        Assert.Equal("\"UPID:pve-node-1:0001\"", await response.Content.ReadAsStringAsync(Ct));

        var info = (ProxmoxVmInfo)ProxmoxCall(method)[0];
        Assert.Equal(Vmid, info.Id);
        Assert.Equal(Node, info.Node);
    }

    /// <summary>
    /// <c>CheckProxmoxTasksBehavior</c> wakes the task poller for any request marked
    /// <c>ICheckProxmoxTasksRequest</c>, rather than leaving the Vm to look idle until the next scheduled
    /// poll. Which requests carry the marker is the map worth pinning: the four power operations and a
    /// revert do, because each starts a cluster task that changes power state; taking and deleting a
    /// snapshot do not.
    /// </summary>
    [Theory]
    [InlineData("POST", "/actions/power-on", true)]
    [InlineData("POST", "/actions/power-off", true)]
    [InlineData("POST", "/actions/reboot", true)]
    [InlineData("POST", "/actions/shutdown", true)]
    [InlineData("POST", "/actions/snapshots/snap-1/revert", true)]
    [InlineData("POST", "/actions/snapshots", false)]
    [InlineData("DELETE", "/actions/snapshots/snap-1", false)]
    [InlineData("POST", "/actions/read-guest-file", false)]
    public async Task WhetherARouteWakesTheTaskPoller(string method, string suffix, bool wakes)
    {
        var vm = await SeedProxmoxVm();

        var response = await Send(method, suffix, vm.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        if (wakes)
        {
            Factory.ProxmoxTasks.Received(1).CheckTasks();
        }
        else
        {
            Factory.ProxmoxTasks.DidNotReceive().CheckTasks();
        }
    }

    #endregion

    #region Reading a Vm

    [Fact]
    public async Task Get_ReturnsTheVmWithTheNetworksTheCallerMaySelect()
    {
        var vm = await SeedProxmoxVm();
        await Seed(Network("vmbr9", "red-team-lan"));

        var result = await Get<ProxmoxVirtualMachine>(Route(vm.Id));

        Assert.Equal(vm.Id, result.Id);
        Assert.Equal(vm.Name, result.Name);
        Assert.True(result.CanAccessNicConfiguration);

        // Registered for this View, this provider and this cluster, so it reaches the NIC options as a
        // selectable network under the name it was registered with.
        var allowed = (IDictionary<string, string>)ProxmoxCall(nameof(IProxmoxService.GetNicOptions))[1];
        Assert.Equal("red-team-lan", Assert.Contains("vmbr9", allowed));
    }

    /// <summary>
    /// With nothing registered there is nothing to choose between, and the UI reads this flag to decide
    /// whether to offer the control at all.
    /// </summary>
    [Fact]
    public async Task Get_WithNoNetworksRegistered_ReportsTheNicConfigurationAsUnavailable()
    {
        var vm = await SeedProxmoxVm();

        var result = await Get<ProxmoxVirtualMachine>(Route(vm.Id));

        Assert.False(result.CanAccessNicConfiguration);
        Assert.Empty((IDictionary<string, string>)ProxmoxCall(nameof(IProxmoxService.GetNicOptions))[1]);
    }

    /// <summary>
    /// The ISO provider is registered for Proxmox and this Vm is a QEMU Vm with a drive, which is what
    /// the flag is the conjunction of. The three ways it can be false are
    /// <see cref="ProxmoxVmNetworkServiceTests"/>; what only a request shows is that the provider
    /// registration reaches it at all.
    /// </summary>
    [Fact]
    public async Task Get_WhenProxmoxIsoStorageIsConfigured_OffersTheMountControl()
    {
        var vm = await SeedProxmoxVm();

        Assert.True((await Get<ProxmoxVirtualMachine>(Route(vm.Id))).CanMountIso);
    }

    [Fact]
    public async Task GetConsole_ReturnsTheTicketAndTheLivePowerState()
    {
        var vm = await SeedProxmoxVm();
        Factory.Proxmox.GetConsole(TheVm).Returns(new ProxmoxConsole
        {
            Url = "wss://pve.test/api2/json/nodes/pve-node-1/qemu/101/vncwebsocket",
            Ticket = "PVEVNC:ticket",
            PowerState = PowerState.On,
        });

        var response = await RootClient.GetAsync($"{Route(vm.Id)}/console", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var console = await response.Content.ReadFromJsonAsync<ConsoleResponse>(JsonOptions, Ct);
        Assert.Equal("PVEVNC:ticket", console.Ticket);
        Assert.Equal("wss://pve.test/api2/json/nodes/pve-node-1/qemu/101/vncwebsocket", console.Url);
        Assert.Equal(PowerState.On, console.PowerState);

        // A generated client reads the state as a name, not a number. Asserted against the raw body
        // because JsonOptions comes from the host and would follow the converter being removed.
        Assert.Contains("\"powerState\":\"On\"", await response.Content.ReadAsStringAsync(Ct));
    }

    #endregion

    #region Snapshots

    [Fact]
    public async Task GetSnapshots_ReturnsWhatTheClusterReports()
    {
        var vm = await SeedProxmoxVm();
        Factory.Proxmox.GetSnapshots(TheVm).Returns(
        [
            new ProxmoxSnapshot
            {
                Name = "before-patch",
                Description = "taken by the exercise author",
                Parent = "current",
                VmState = true,
                SnapTime = 1700000000,
            },
        ]);

        var snapshot = Assert.Single(
            await Get<ProxmoxSnapshot[]>($"{Route(vm.Id)}/snapshots"));

        Assert.Equal("before-patch", snapshot.Name);
        Assert.Equal("taken by the exercise author", snapshot.Description);
        Assert.Equal("current", snapshot.Parent);
        Assert.True(snapshot.VmState);
        Assert.Equal(1700000000, snapshot.SnapTime);
    }

    [Fact]
    public async Task CreateSnapshot_PassesTheNameDescriptionAndRamFlag()
    {
        var vm = await SeedProxmoxVm();
        Factory.Proxmox
            .CreateSnapshot(Arg.Any<ProxmoxVmInfo>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns("UPID:pve-node-1:0002");

        var response = await Post(
            $"{Route(vm.Id)}/actions/snapshots",
            new { SnapshotName = "before-patch", Description = "for the reset", IncludeRam = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"UPID:pve-node-1:0002\"", await response.Content.ReadAsStringAsync(Ct));

        var call = ProxmoxCall(nameof(IProxmoxService.CreateSnapshot));
        Assert.Equal("before-patch", call[1]);
        Assert.Equal("for the reset", call[2]);
        Assert.Equal(true, call[3]);
    }

    /// <summary>
    /// The snapshot name is a route segment on both of these, not a body field, so it arrives through URL
    /// decoding - which is worth one assertion, because a name with a space in it is what an author
    /// typing into the UI produces.
    /// </summary>
    [Theory]
    [InlineData("POST", "/revert", nameof(IProxmoxService.RevertSnapshot))]
    [InlineData("DELETE", "", nameof(IProxmoxService.DeleteSnapshot))]
    public async Task SnapshotRoutes_TakeTheNameFromThePath(string method, string tail, string serviceMethod)
    {
        var vm = await SeedProxmoxVm();

        var response = await Send(method, $"/actions/snapshots/before%20patch{tail}", vm.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("before patch", ProxmoxCall(serviceMethod)[1]);
    }

    #endregion

    #region Guest agent

    [Fact]
    public async Task RunGuestProcess_ReturnsTheGuestResultAndUsesTheRequestedTimeout()
    {
        var vm = await SeedProxmoxVm();
        Factory.Proxmox
            .RunGuestProcess(Arg.Any<ProxmoxVmInfo>(), "/bin/sh", "-c id", Arg.Any<TimeSpan>())
            .Returns(new GuestProcessResult { Output = "uid=0(root)", ExitCode = 0, Success = true });

        var response = await Post(
            $"{Route(vm.Id)}/actions/run-guest-process",
            new { ProgramPath = "/bin/sh", Arguments = "-c id", TimeoutSeconds = 12 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GuestProcessResult>(JsonOptions, Ct);
        Assert.Equal("uid=0(root)", result.Output);
        Assert.True(result.Success);

        Assert.Equal(
            TimeSpan.FromSeconds(12),
            ProxmoxCall(nameof(IProxmoxService.RunGuestProcess))[3]);
    }

    /// <summary>
    /// No timeout in the request means the configured default, not "wait forever" - a guest process that
    /// never exits would otherwise hold a request thread and a guest-agent slot indefinitely.
    /// </summary>
    /// <remarks>
    /// Note what the command carries and the service does not: <c>WorkingDirectory</c>. It binds, and
    /// then nothing reads it - <c>IProxmoxService.RunGuestProcess</c> has no parameter for it. Accepted
    /// and ignored on both guest-process routes.
    /// </remarks>
    [Fact]
    public async Task RunGuestProcess_WithNoTimeout_UsesTheConfiguredDefault()
    {
        var vm = await SeedProxmoxVm();
        var configured = Factory.Services.GetRequiredService<IOptions<ProxmoxOptions>>()
            .Value.GuestProcessDefaultTimeoutSeconds;

        // Otherwise this passes just as well against a host that read no configuration at all.
        Assert.NotEqual(0, configured);

        var response = await Post(
            $"{Route(vm.Id)}/actions/run-guest-process",
            new { ProgramPath = "/bin/true", WorkingDirectory = "/root" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            TimeSpan.FromSeconds(configured),
            ProxmoxCall(nameof(IProxmoxService.RunGuestProcess))[3]);
    }

    /// <summary>
    /// The fast variant answers with the guest agent's pid rather than the process output, which is what
    /// a caller polls exec-status with.
    /// </summary>
    [Fact]
    public async Task RunGuestProcessFast_ReturnsThePid()
    {
        var vm = await SeedProxmoxVm();
        Factory.Proxmox.RunGuestProcessFast(TheVm, "/bin/sh", "-c reboot").Returns(4242L);

        var response = await Post(
            $"{Route(vm.Id)}/actions/run-guest-process-fast",
            new { ProgramPath = "/bin/sh", Arguments = "-c reboot" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("4242", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The guest path is passed to the agent exactly as submitted.
    /// </summary>
    /// <remarks>
    /// Including one that looks like traversal, which is deliberate rather than an oversight: the agent
    /// reads as root inside the guest, so every absolute path in it is already reachable and there is no
    /// boundary for a relative one to escape. The permission is the whole of the gate, which is why the
    /// 403 case above is the test that matters.
    /// </remarks>
    [Theory]
    [InlineData("/etc/hosts")]
    [InlineData("../../etc/shadow")]
    public async Task ReadGuestFile_PassesThePathThroughUnchanged(string path)
    {
        var vm = await SeedProxmoxVm();
        Factory.Proxmox.ReadGuestFile(TheVm, path).Returns("127.0.0.1 localhost");

        var response = await Post(
            $"{Route(vm.Id)}/actions/read-guest-file", new { GuestFilePath = path });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"127.0.0.1 localhost\"", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(path, ProxmoxCall(nameof(IProxmoxService.ReadGuestFile))[1]);
    }

    /// <summary>
    /// Every file in the form is written, each under the submitted path.
    /// </summary>
    [Fact]
    public async Task UploadFile_WritesEveryFileInTheFormUnderTheSubmittedPath()
    {
        var vm = await SeedProxmoxVm();

        var form = new MultipartFormDataContent
        {
            { new StringContent("/tmp/"), "filePath" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("one")), "files", "a.txt" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("two")), "files", "b.txt" },
        };

        var response = await RootClient.PostAsync($"{Route(vm.Id)}/actions/upload-file", form, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"Files were successfully uploaded.\"", await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal<string>(
            ["/tmp/a.txt", "/tmp/b.txt"],
            ProxmoxCalls(nameof(IProxmoxService.UploadFileToGuest)).Select(x => (string)x[1]));
    }

    /// <summary>A path with no trailing separator is joined to each filename as is.</summary>
    [Fact]
    public async Task UploadFile_ToAPathWithNoTrailingSeparator_WritesThePathAndFilenameRunTogether()
    {
        var vm = await SeedProxmoxVm();

        var form = new MultipartFormDataContent
        {
            { new StringContent("/tmp"), "filePath" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("one")), "files", "a.txt" },
        };

        var response = await RootClient.PostAsync($"{Route(vm.Id)}/actions/upload-file", form, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/tmpa.txt", (string)ProxmoxCall(nameof(IProxmoxService.UploadFileToGuest))[1]);
    }

    /// <summary>
    /// A guest agent that refuses the write is a 400 naming the reason, not a 500: the payload ceiling it
    /// enforces is something the caller can act on, and a 500 would have a client retrying a request that
    /// can never succeed.
    /// </summary>
    [Fact]
    public async Task UploadFile_WhenTheGuestAgentRefuses_Is400NamingTheReason()
    {
        var vm = await SeedProxmoxVm();
        Factory.Proxmox
            .UploadFileToGuest(Arg.Any<ProxmoxVmInfo>(), Arg.Any<string>(), Arg.Any<Stream>())
            .ThrowsAsync(new InvalidOperationException("File exceeds the 61440 byte limit"));

        var form = new MultipartFormDataContent
        {
            { new StringContent("/tmp/"), "filePath" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("too big")), "files", "a.txt" },
        };

        var response = await RootClient.PostAsync($"{Route(vm.Id)}/actions/upload-file", form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("File exceeds the 61440 byte limit", await Title(response));
    }

    #endregion

    #region ISOs

    /// <summary>
    /// The picker is built from the storage this Vm's node can reach, not from everything the provider
    /// holds: the mount values come straight back to <c>mount-iso</c>, so a volume on storage this node
    /// cannot see would be offered and then fail to mount.
    /// </summary>
    [Fact]
    public async Task GetIsos_ListsFromTheStorageThisVmCanReach()
    {
        var vm = await SeedProxmoxVm();
        IsoScopeIsResolvable();
        Factory.IsoProvider.ListForVmAsync(vm.Id, _viewId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<IsoListingEntry>>
            {
                [_viewId] = [new IsoListingEntry("shared.iso", "isos:iso/shared.iso")],
                [_teamId] = [new IsoListingEntry("team.iso", "isos:iso/team.iso")],
            });

        var result = Assert.Single(await Get<MountableIsoResult[]>($"{Route(vm.Id)}/isos"));

        Assert.Equal(_viewId, result.ViewId);
        Assert.Equal("view-1", result.ViewName);
        Assert.Equal<string>(["shared.iso"], result.Isos.Select(x => x.Filename));

        var team = Assert.Single(result.TeamIsoResults);
        Assert.Equal(_teamId, team.TeamId);
        Assert.Equal<string>(["isos:iso/team.iso"], team.Isos.Select(x => x.MountValue));

        await Factory.IsoProvider.DidNotReceive().ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A caller controlling every Vm through the system permission, with no team in the View, is answered the ISO picker with a 500.</summary>
    [Fact]
    public async Task GetIsos_answers_a_caller_holding_ControlVms_with_no_team_in_the_view_with_a_server_error()
    {
        var vm = await SeedProxmoxVm();
        var actor = await Actor().Holding(AppSystemPermission.ControlVms).SeedAsync();

        var response = await Client(actor).GetAsync($"{Route(vm.Id)}/isos", Ct);

        Assert.StartsWith("getView: Forbidden", (await AssertJsonError(HttpStatusCode.InternalServerError, response)).Detail);
    }

    [Fact]
    public async Task GetIsos_offers_the_teams_isos_to_a_caller_holding_exactly_ControlTeamVms_on_the_Vms_team()
    {
        var vm = await SeedProxmoxVm();
        TheVmCanReachATeamIso(vm);
        var actor = await Actor().Holding(AppTeamPermission.ControlTeamVms, _teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"{Route(vm.Id)}/isos", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.Single(await response.Content.ReadFromJsonAsync<MountableIsoResult[]>(JsonOptions, Ct));
        Assert.Equal(_teamId, Assert.Single(result.TeamIsoResults).TeamId);
    }

    // Seeing the Vm lists its picker, but a team's ISOs are offered only to a caller who may mount them.
    [Fact]
    public async Task GetIsos_leaves_out_the_teams_isos_for_a_caller_holding_only_ViewTeamVms_on_the_Vms_team()
    {
        var vm = await SeedProxmoxVm();
        TheVmCanReachATeamIso(vm);
        var actor = await Actor().Holding(AppTeamPermission.ViewTeamVms, _teamId).SeedAsync();

        var response = await Client(actor).GetAsync($"{Route(vm.Id)}/isos", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Assert.Single(await response.Content.ReadFromJsonAsync<MountableIsoResult[]>(JsonOptions, Ct)).TeamIsoResults);
    }

    /// <summary>
    /// A Vm whose teams player.api places in no View has no ISO scope to list, and that is an empty
    /// picker rather than an error.
    /// </summary>
    [Fact]
    public async Task GetIsos_ForAVmInNoView_IsAnEmptyList()
    {
        var vm = await SeedProxmoxVm();
        Factory.Views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        Assert.Empty(await Get<MountableIsoResult[]>($"{Route(vm.Id)}/isos"));
    }

    /// <summary>
    /// The submitted volume id is never what gets mounted. A PVE volid can name any volume in the
    /// cluster - including another Vm's disk image - so it is decoded to a scope, authorized against
    /// this Vm, and rebuilt; the rebuilt value is what reaches the cluster.
    /// </summary>
    [Fact]
    public async Task MountIso_MountsTheVolumeTheProviderResolvedRatherThanTheOneSubmitted()
    {
        var vm = await SeedProxmoxVm();
        Factory.IsoProvider
            .ResolveMountTargetAsync(vm.Id, "isos:iso/submitted.iso", Arg.Any<CancellationToken>())
            .Returns(new IsoMountTarget(_viewId, _viewId, "boot.iso", "isos:iso/canonical.iso"));

        var response = await Post(
            $"{Route(vm.Id)}/actions/mount-iso", new { Iso = "isos:iso/submitted.iso" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("isos:iso/canonical.iso", ProxmoxCall(nameof(IProxmoxService.MountIso))[1]);
    }

    /// <summary>
    /// Nothing the provider will not vouch for is mounted. Which values it vouches for is
    /// <see cref="IsoServiceMountAuthTests"/>; what matters here is that a refusal stops the request
    /// before the cluster is touched.
    /// </summary>
    [Fact]
    public async Task MountIso_WithAVolumeTheProviderWillNotVouchFor_Is403AndMountsNothing()
    {
        var vm = await SeedProxmoxVm();

        var response = await Post(
            $"{Route(vm.Id)}/actions/mount-iso", new { Iso = "local:iso/somebody-elses.iso" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The specified iso is not available to this Vm", await Title(response));

        await Factory.Proxmox.DidNotReceive()
            .MountIso(Arg.Any<ProxmoxVmInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MountIso_of_another_teams_iso_is_allowed_for_a_caller_holding_exactly_ControlTeamVms_on_both_teams()
    {
        var vm = await SeedProxmoxVm();
        var otherTeam = AnotherTeamsIsoIsSubmitted(vm);
        var actor = await Actor()
            .Holding(AppTeamPermission.ControlTeamVms, _teamId)
            .Holding(AppTeamPermission.ControlTeamVms, otherTeam)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            $"{Route(vm.Id)}/actions/mount-iso", new { Iso = "isos:iso/submitted.iso" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("isos:iso/canonical.iso", ProxmoxCall(nameof(IProxmoxService.MountIso))[1]);
    }

    [Fact]
    public async Task MountIso_of_another_teams_iso_is_forbidden_for_a_caller_holding_ControlTeamVms_only_on_the_Vms_team()
    {
        var vm = await SeedProxmoxVm();
        AnotherTeamsIsoIsSubmitted(vm);
        var actor = await Actor().Holding(AppTeamPermission.ControlTeamVms, _teamId).SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            $"{Route(vm.Id)}/actions/mount-iso", new { Iso = "isos:iso/submitted.iso" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The specified iso is not available to this Vm", await Title(response));

        await Factory.Proxmox.DidNotReceive()
            .MountIso(Arg.Any<ProxmoxVmInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// <c>ControlViewVms</c> in the View controls every team's Vms there, so it passes both the editing gate
    /// and the check on the submitted ISO's team.
    /// </summary>
    [Fact]
    public async Task MountIso_of_another_teams_iso_is_allowed_for_a_caller_holding_exactly_ControlViewVms_on_the_view()
    {
        var vm = await SeedProxmoxVm();
        AnotherTeamsIsoIsSubmitted(vm);
        var actor = await Actor()
            .Holding(AppViewPermission.ControlViewVms, _viewId)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            $"{Route(vm.Id)}/actions/mount-iso", new { Iso = "isos:iso/submitted.iso" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("isos:iso/canonical.iso", ProxmoxCall(nameof(IProxmoxService.MountIso))[1]);
    }

    /// <summary>
    /// <c>ControlTeamVms</c> on the Vm's team passes the editing gate, so the refusal is the ISO team check's,
    /// and <c>ControlViewVms</c> is held in another View, where it does not reach the ISO's team.
    /// </summary>
    [Fact]
    public async Task MountIso_of_another_teams_iso_is_forbidden_for_a_caller_holding_ControlViewVms_only_on_another_view()
    {
        var vm = await SeedProxmoxVm();
        AnotherTeamsIsoIsSubmitted(vm);
        var actor = await Actor()
            .Holding(AppTeamPermission.ControlTeamVms, _teamId)
            .Holding(AppViewPermission.ControlViewVms, Guid.NewGuid())
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            $"{Route(vm.Id)}/actions/mount-iso", new { Iso = "isos:iso/submitted.iso" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The specified iso is not available to this Vm", await Title(response));

        await Factory.Proxmox.DidNotReceive()
            .MountIso(Arg.Any<ProxmoxVmInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MountIso_of_another_teams_iso_is_allowed_for_a_caller_holding_exactly_the_system_ControlVms()
    {
        var vm = await SeedProxmoxVm();
        AnotherTeamsIsoIsSubmitted(vm);
        var actor = await Actor()
            .Holding(AppSystemPermission.ControlVms)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            $"{Route(vm.Id)}/actions/mount-iso", new { Iso = "isos:iso/submitted.iso" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("isos:iso/canonical.iso", ProxmoxCall(nameof(IProxmoxService.MountIso))[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MountIso_WithNoIso_Is400(string iso)
    {
        var vm = await SeedProxmoxVm();

        var response = await Post($"{Route(vm.Id)}/actions/mount-iso", new { Iso = iso });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("An iso is required", await Title(response));
    }

    #endregion

    #region Changing a network

    [Fact]
    public async Task ChangeNetwork_ToAnAllowedNetwork_ChangesItAndReturnsTheVm()
    {
        var vm = await SeedProxmoxVm();
        await Seed(Network("vmbr9"));

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network", new { Adapter = "net0", Network = "vmbr9" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(vm.Id, (await response.Content
            .ReadFromJsonAsync<ProxmoxVirtualMachine>(JsonOptions, Ct)).Id);

        var call = ProxmoxCall(nameof(IProxmoxService.ChangeNetwork));
        Assert.Equal("net0", call[1]);
        Assert.Equal("vmbr9", call[2]);
    }

    /// <summary>
    /// Seeing the Vm is the whole of the permission the route asks for; the network shared with the
    /// caller's team is what makes the target one it may choose.
    /// </summary>
    [Fact]
    public async Task ChangeNetwork_is_allowed_for_a_caller_holding_exactly_ViewTeamVms_on_the_Vms_team()
    {
        var vm = await SeedProxmoxVm();
        await Seed(Network("vmbr9"));
        var actor = await Actor()
            .Holding(AppTeamPermission.ViewTeamVms, _teamId)
            .SeedAsync();

        var response = await Client(actor).PostAsJsonAsync(
            $"{Route(vm.Id)}/actions/change-network", new { Adapter = "net0", Network = "vmbr9" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("vmbr9", ProxmoxCall(nameof(IProxmoxService.ChangeNetwork))[2]);
    }

    /// <summary>
    /// No networks registered for the View means no permission to change one - the allowed list is the
    /// permission, so an empty one is a refusal rather than an empty menu.
    /// </summary>
    [Fact]
    public async Task ChangeNetwork_WithNothingRegisteredForTheView_Is403()
    {
        var vm = await SeedProxmoxVm();

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network", new { Adapter = "net0", Network = "vmbr9" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You do not have permission to change networks on this VM", await Title(response));

        await AssertNoNetworkChange();
    }

    /// <summary>
    /// The submitted network is checked against the allowed list rather than trusted, which is what stops
    /// a caller naming a bridge that carries another team's traffic.
    /// </summary>
    [Fact]
    public async Task ChangeNetwork_ToANetworkOutsideTheAllowedList_Is403()
    {
        var vm = await SeedProxmoxVm();
        await Seed(Network("vmbr9"));

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network", new { Adapter = "net0", Network = "vmbr50" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The target network is not in your allowed networks list", await Title(response));

        await AssertNoNetworkChange();
    }

    /// <summary>
    /// The row has to belong to the cluster this instance is configured against. Two vm.api instances
    /// serving one Player deployment each register their own bridges, and a bridge id means nothing
    /// outside the cluster that defines it.
    /// </summary>
    [Fact]
    public async Task ChangeNetwork_ToANetworkRegisteredForAnotherCluster_Is403()
    {
        var vm = await SeedProxmoxVm();
        var elsewhere = Network("vmbr9");
        elsewhere.ProviderInstanceId = "pve-other.test";
        await Seed(elsewhere);

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network", new { Adapter = "net0", Network = "vmbr9" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You do not have permission to change networks on this VM", await Title(response));

        await AssertNoNetworkChange();
    }

    [Theory]
    [InlineData(null, "vmbr9")]
    [InlineData("", "vmbr9")]
    [InlineData("   ", "vmbr9")]
    [InlineData("net0", null)]
    [InlineData("net0", "")]
    public async Task ChangeNetwork_WithoutBothAnAdapterAndANetwork_Is400(string adapter, string network)
    {
        var vm = await SeedProxmoxVm();
        await Seed(Network("vmbr9"));

        var response = await Post(
            $"{Route(vm.Id)}/actions/change-network", new { Adapter = adapter, Network = network });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("An adapter and target network are required", await Title(response));

        await AssertNoNetworkChange();
    }

    #endregion

    #region Helpers

    private static string Route(Guid id) => $"/api/vms/proxmox/{id}";

    /// <summary>
    /// Matches the <c>ProxmoxVmInfo</c> of the seeded Vm by value. The handler loads its own instance
    /// through its own <c>VmContext</c>, so reference equality is not available.
    /// </summary>
    private static ProxmoxVmInfo TheVm =>
        Arg.Is<ProxmoxVmInfo>(x => x != null && x.Id == Vmid && x.Node == Node);

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

        return (client ?? RootClient).SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> Post(string route, object body) =>
        RootClient.PostAsJsonAsync(route, body, Ct);

    private async Task<T> Get<T>(string route)
    {
        var response = await RootClient.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    /// <summary>The message the exception middleware put in the ProblemDetails body.</summary>
    private async Task<string> Title(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct))
            .RootElement.GetProperty("title").GetString();

    /// <summary>A Proxmox Vm on this test's team, saved.</summary>
    private async Task<VmEntity> SeedProxmoxVm(
        ProxmoxVmType type = ProxmoxVmType.QEMU, Guid? userId = null)
    {
        var id = Guid.NewGuid();

        var vm = new VmEntity
        {
            Id = id,
            Name = $"proxmox-{id}",
            Type = VmType.Proxmox,
            UserId = userId,
            VmTeams = [new VmTeam(_teamId, id)],
            ProxmoxVmInfo = new ProxmoxVmInfo { Id = Vmid, Node = Node, Type = type },
        };

        await Seed(vm);

        return vm;
    }

    /// <summary>
    /// A network registered for this test's View, on the cluster this host is configured against, and
    /// shared with this test's team - which is what makes it selectable for a caller who holds no
    /// view-wide network permission.
    /// </summary>
    private ViewNetwork Network(string networkId, string name = null) =>
        new()
        {
            ViewId = _viewId,
            ProviderType = VmType.Proxmox,
            ProviderInstanceId = VmAppFactory.ProxmoxHost,
            NetworkId = networkId,
            Name = name ?? networkId,
            TeamIds = [_teamId],
        };

    /// <summary>
    /// Wires the View resolution the ISO listing walks: the Vm's team sits in this test's View, the
    /// caller is a member of it, and player.api can name both.
    /// </summary>
    private void IsoScopeIsResolvable()
    {
        Factory.Player.PlaceTeam(_teamId, _viewId, "team-1");
        Factory.Player.AddView(_viewId, "view-1");
        Factory.Player.Caller(Root.Id).ClaimOn(_teamId).Primary = true;
    }

    /// <summary>The storage this Vm's node can reach holds one ISO of the Vm's team.</summary>
    private void TheVmCanReachATeamIso(VmEntity vm) =>
        Factory.IsoProvider.ListForVmAsync(vm.Id, _viewId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<IsoListingEntry>>
            {
                [_teamId] = [new IsoListingEntry("team.iso", "isos:iso/team.iso")],
            });

    /// <summary>
    /// A second team of this test's View, and the submitted mount value decoding to an ISO of that team's:
    /// the scope <c>IsoService.ResolveMountValueAsync</c> asks the caller's control of.
    /// </summary>
    private Guid AnotherTeamsIsoIsSubmitted(VmEntity vm)
    {
        var otherTeam = Guid.NewGuid();
        Factory.Player.PlaceTeam(otherTeam, _viewId);
        Factory.IsoProvider
            .ResolveMountTargetAsync(vm.Id, "isos:iso/submitted.iso", Arg.Any<CancellationToken>())
            .Returns(new IsoMountTarget(_viewId, otherTeam, "boot.iso", "isos:iso/canonical.iso"));

        return otherTeam;
    }

    private void PowerCommandsReturn(string taskId)
    {
        Factory.Proxmox.PowerOnVm(Arg.Any<ProxmoxVmInfo>()).Returns(taskId);
        Factory.Proxmox.PowerOffVm(Arg.Any<ProxmoxVmInfo>()).Returns(taskId);
        Factory.Proxmox.RebootVm(Arg.Any<ProxmoxVmInfo>()).Returns(taskId);
        Factory.Proxmox.ShutdownVm(Arg.Any<ProxmoxVmInfo>()).Returns(taskId);
    }

    private static readonly AppSystemPermission[] NetworkPermissions =
        [AppSystemPermission.ViewNetworks, AppSystemPermission.ManageNetworks];

    /// <summary>
    /// A caller holding every permission Root does except <paramref name="permission"/> and the system and
    /// team permissions that answer the same gate (<c>ControlVms</c> and <c>ControlTeamVms</c> for
    /// <c>ControlViewVms</c>, the system permission of the same name for the snapshot and guest-file
    /// permissions): every other system permission, every other View permission on this test's View and
    /// every team permission on the Vm's team. A 403 can only have come from the gate that asks for
    /// <paramref name="permission"/>.
    /// </summary>
    private async Task<HttpClient> AllBut(AppViewPermission permission)
    {
        AppSystemPermission? system = permission switch
        {
            AppViewPermission.ControlViewVms => AppSystemPermission.ControlVms,
            AppViewPermission.RevertVms => AppSystemPermission.RevertVms,
            AppViewPermission.DownloadVmFiles => AppSystemPermission.DownloadVmFiles,
            AppViewPermission.UploadVmFiles => AppSystemPermission.UploadVmFiles,
            _ => null,
        };
        AppTeamPermission? team = permission == AppViewPermission.ControlViewVms ? AppTeamPermission.ControlTeamVms : null;

        var builder = Actor().MemberOf(_teamId)
            .Holding([.. Enum.GetValues<AppSystemPermission>().Except(NetworkPermissions).Where(x => x != system)]);

        foreach (var view in Enum.GetValues<AppViewPermission>().Where(x => x != permission))
            builder.Holding(view, _viewId);

        foreach (var teamPermission in Enum.GetValues<AppTeamPermission>().Where(x => x != team))
            builder.Holding(teamPermission, _teamId);

        return Client(await builder.SeedAsync());
    }

    /// <summary>
    /// A plain participant on the Vm's team: <c>ViewTeamVms</c> on it, and nothing else - no system
    /// permission, no View permission.
    /// </summary>
    private async Task<HttpClient> TeamParticipant() =>
        Client(await Actor().Holding(AppTeamPermission.ViewTeamVms, _teamId).SeedAsync());

    /// <summary>The arguments of the single call to the named <c>IProxmoxService</c> method.</summary>
    private object[] ProxmoxCall(string methodName) => Assert.Single(ProxmoxCalls(methodName));

    /// <summary>The arguments of each call to the named <c>IProxmoxService</c> method, in order.</summary>
    private object[][] ProxmoxCalls(string methodName) =>
        [.. Factory.Proxmox.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == methodName)
            .Select(x => x.GetArguments())];

    private Task AssertNoNetworkChange() =>
        Factory.Proxmox.DidNotReceive().ChangeNetwork(
            Arg.Any<ProxmoxVmInfo>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

    #endregion
}
