// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Extensions;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Tests.Support;

namespace Player.Vm.Api.Tests.Domain.Proxmox;

public class ProxmoxExtensionsTests
{
    // The name a Proxmox failure reaches the user under: the UI's own label for the action, never the
    // identifiers the caller added for the log.
    [Theory]
    [InlineData("PowerOn vmid=100", "Power On")]
    [InlineData("PowerOff vmid=100", "Power Off")]
    [InlineData("Reboot vmid=100", "Reboot")]
    [InlineData("Shutdown vmid=100", "Shutdown")]
    [InlineData("CreateSnapshot vmid=100 name=snap1", "Create Snapshot")]
    [InlineData("RevertSnapshot vmid=100 name=snap1", "Revert to Snapshot")]
    [InlineData("RevertToCurrentSnapshot vmid=100", "Revert")]
    [InlineData("DeleteSnapshot vmid=100 name=snap1", "Delete Snapshot")]
    [InlineData("MountIso vmid=100 drive=ide2", "Mount ISO")]
    [InlineData("ChangeNetwork vmid=100 adapter=net0", "Change Network")]
    [InlineData("UploadIso node=pve1 file=a.iso", "Upload ISO")]
    [InlineData("DeleteIso node=pve1 file=a.iso", "Delete ISO")]
    public void OperationName_IsTheActionsUiLabel(string operation, string expected)
    {
        Assert.Equal(expected, ProxmoxExtensions.OperationName(operation));
    }

    // An operation without a label still loses its identifiers rather than coming out blank.
    [Fact]
    public void OperationName_ForAnUnlabelledOperation_IsItsFirstWord()
    {
        Assert.Equal("Resize", ProxmoxExtensions.OperationName("Resize vmid=100"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void OperationName_ForNoOperation_IsNull(string operation)
    {
        Assert.Null(ProxmoxExtensions.OperationName(operation));
    }

    // A task still running when the wait runs out is a 504: Proxmox took the request and has not
    // answered, which is neither the caller's mistake nor a fault here. The message names the action
    // and says the task may yet finish, because it is not cancelled.
    [Fact]
    public async Task WaitAndThrow_WhenTheTaskOutlastsTheTimeout_IsAGatewayTimeoutNamingTheAction()
    {
        var cluster = new FakeProxmoxCluster();
        var path = FakeProxmoxCluster.VmPath("pve1", ProxmoxVmType.QEMU, 100, "/status/start");
        cluster.SubmitsTaskThatNeverFinishes($"POST {path}");
        var client = cluster.Client();
        var result = await client.Nodes["pve1"].Qemu[100].Status.Start.VmStart();

        var ex = await Assert.ThrowsAsync<ProxmoxTaskTimeoutException>(
            () => client.WaitAndThrow(result, "PowerOn vmid=100", TestContext.Current.CancellationToken, wait: 10, timeout: 60));

        Assert.Equal(HttpStatusCode.GatewayTimeout, ex.GetStatusCode());
        Assert.IsAssignableFrom<TimeoutException>(ex);
        Assert.StartsWith("Power On did not finish within ", ex.Message);
        Assert.Contains("may still be running", ex.Message);
        Assert.Contains(cluster.Http.Paths, x => x.Contains("/tasks/") && x.EndsWith("/status"));
    }

    // A status poll that answers after the deadline but reports the task done is a finished task:
    // reporting it as a 504 that "may still be running" would tell the user something false.
    [Fact]
    public async Task WaitAndThrow_WhenTheTaskFinishesOnAPollPastTheDeadline_Succeeds()
    {
        var cluster = new FakeProxmoxCluster();
        var path = FakeProxmoxCluster.VmPath("pve1", ProxmoxVmType.QEMU, 100, "/status/start");
        var upid = $"UPID:pve1:0000ABCD:0011:0022:qmstart:100:{FakeProxmoxCluster.ApiToken}:";
        cluster.Http.AnswersJson($"POST {path}", FakeProxmoxCluster.Data($"\"{upid}\""));
        cluster.Http.AnswersJson($"GET api2/json/nodes/pve1/tasks/{upid}/status", () =>
        {
            // Outlasts the whole 60 ms budget, then reports the task stopped.
            Thread.Sleep(150);
            return FakeProxmoxCluster.Data("{\"status\":\"stopped\",\"exitstatus\":\"OK\"}");
        });
        var client = cluster.Client();
        var result = await client.Nodes["pve1"].Qemu[100].Status.Start.VmStart();

        await client.WaitAndThrow(result, "PowerOn vmid=100", TestContext.Current.CancellationToken, wait: 10, timeout: 60);
    }

    [Theory]
    [InlineData(3600 * 1000, "within 60 minutes.")]
    [InlineData(60 * 1000, "within 1 minute.")]
    [InlineData(90 * 1000, "within 90 seconds.")]
    public void TaskTimeout_StatesTheBudgetInWholeUnits(long timeoutMs, string expected)
    {
        var ex = new ProxmoxTaskTimeoutException("Reboot", TimeSpan.FromMilliseconds(timeoutMs));

        Assert.Contains(expected, ex.Message);
    }
}
