// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Node;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Features.Vms.Hubs;
using Xunit;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// That <see cref="PollLoop"/> itself works, which nothing else in the suite would say: every test built
/// on it asserts what a poller <em>did</em>, and a harness that never turned the loop at all would report
/// that as "the poller did nothing" rather than as a broken harness.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="DatabaseHarnessTests"/>, and driven through <c>ProxmoxTaskService</c>
/// because it is the poller with the fewest collaborators. What is asserted is only the harness's own
/// contract - a pass happens, a pass is one scope, an extra pass is refused, and the service stops -
/// never what the service makes of anything.
/// </remarks>
public class PollLoopSmokeTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>
    /// A minute, so that nothing but the harness's nudge can advance the loop. Every test built on
    /// <see cref="PollLoop.Run"/> configures it this way; if the interval were what turned the loop, a
    /// test would be a race rather than an assertion.
    /// </summary>
    private const int NeverOnItsOwn = 60_000;

    [Fact]
    public async Task Run_TurnsTheLoopOnceAndStopsIt()
    {
        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetTasks().Returns(Task.FromResult<IEnumerable<NodeTask>>([]));
        var loop = new PollLoop(NewContext, proxmox);
        var service = Service(loop);

        await loop.Run(service, service.CheckTasks);

        // The pass really ran: the poller asked the cluster for its tasks. And it ran once - the scopes
        // asked for after it were refused, so no second pass touched anything.
        await proxmox.Received(1).GetTasks();
        Assert.Single(loop.Contexts);
        Assert.True(loop.Passes > 1, "a refused pass is the barrier, so one must have been attempted");
    }

    [Fact]
    public async Task Run_ForMoreThanOnePass_TurnsTheLoopThatManyTimes()
    {
        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetTasks().Returns(Task.FromResult<IEnumerable<NodeTask>>([]));
        var loop = new PollLoop(NewContext, proxmox);
        var service = Service(loop);

        await loop.Run(service, service.CheckTasks, passes: 3);

        await proxmox.Received(3).GetTasks();

        // A context per pass, which is what makes a pass see what the pass before it committed.
        Assert.Equal(3, loop.Contexts.Count);
    }

    /// <summary>
    /// The refusal is delivered as an exception, and the service swallowing it is what the harness
    /// depends on: a poller that let it out would fault its execute task and stop turning.
    /// </summary>
    [Fact]
    public async Task Run_LeavesTheServiceRunningAfterAPassIsRefused()
    {
        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetTasks().Returns(Task.FromResult<IEnumerable<NodeTask>>([]));
        var loop = new PollLoop(NewContext, proxmox);
        var service = Service(loop);

        await service.StartAsync(Ct);
        loop.AllowedPasses = 1;

        try
        {
            // Two refusals after the one real pass, and the loop is still turning at the end of them.
            await PollLoop.Until(
                () =>
                {
                    service.CheckTasks();

                    return loop.Passes >= 3;
                },
                "three passes to be attempted");
        }
        finally
        {
            service.CheckTasks();
            await service.StopAsync(Ct);
        }

        await proxmox.Received(1).GetTasks();
    }

    /// <summary>
    /// The other entry point: no nudge at all, so the only thing that can turn the loop a second time is
    /// the interval the poller chose. The tests about that choice are built on this, and they are only
    /// meaningful if a short interval really does produce a pass here.
    /// </summary>
    [Fact]
    public async Task RunUnprompted_TurnsTheLoopOnTheIntervalWithNoNudge()
    {
        var proxmox = Substitute.For<IProxmoxService>();
        proxmox.GetTasks().Returns(Task.FromResult<IEnumerable<NodeTask>>([]));
        var loop = new PollLoop(NewContext, proxmox);
        var service = Service(loop, interval: 25);

        await loop.RunUnprompted(service, service.CheckTasks);

        Assert.True(loop.Passes >= 2);
    }

    private ProxmoxTaskService Service(PollLoop loop, int interval = NeverOnItsOwn)
    {
        var options = Substitute.For<IOptionsMonitor<ProxmoxOptions>>();
        options.CurrentValue.Returns(new ProxmoxOptions
        {
            Enabled = true,
            CheckTaskProgressIntervalMilliseconds = interval,
            ReCheckTaskProgressIntervalMilliseconds = interval,
        });

        return new ProxmoxTaskService(
            new RecordingLogger<ProxmoxTaskService>(),
            options,
            loop,
            new HubContextHarness<ProgressHub>().Context);
    }
}
