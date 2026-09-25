// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Options;
using Xunit;

namespace Player.Vm.Api.Tests;

public class VmInitializationQueueTests
{
    private readonly ManualClock _clock = new();
    private readonly IOptionsMonitor<VmInitializationOptions> _options = Substitute.For<IOptionsMonitor<VmInitializationOptions>>();

    public VmInitializationQueueTests() => _options.CurrentValue.Returns(new VmInitializationOptions());

    [Fact]
    public void QuietPeriodResetsButMaximumWaitDoesNot()
    {
        var queue = new VmInitializationBatchQueue(_options, _clock);
        queue.Enqueue(Guid.NewGuid());
        for (var second = 1; second <= 4; second++)
        {
            _clock.Advance(1);
            queue.Enqueue(Guid.NewGuid());
            Assert.Null(queue.TakeReady(out _));
        }
        _clock.Advance(1);
        Assert.Equal(5, queue.TakeReady(out _).Count);
    }

    [Fact]
    public void QuietPeriodGroupsArrivalsWithoutAnIdCap()
    {
        var queue = new VmInitializationBatchQueue(_options, _clock);
        var first = Guid.NewGuid();
        queue.Enqueue(first);
        _clock.Advance(1);
        foreach (var id in Enumerable.Range(0, 250).Select(_ => Guid.NewGuid()))
            queue.Enqueue(id);
        queue.Enqueue(first);

        _clock.Advance(1);
        Assert.Null(queue.TakeReady(out var remaining));
        Assert.Equal(TimeSpan.FromSeconds(1), remaining);
        _clock.Advance(1);
        Assert.Equal(251, queue.TakeReady(out _).Count);
        Assert.Null(queue.TakeReady(out remaining));
        Assert.Null(remaining);
    }

    [Fact]
    public void DuplicateRequestsDoNotExtendTheQuietPeriod()
    {
        var queue = new VmInitializationBatchQueue(_options, _clock);
        var id = Guid.NewGuid();
        queue.Enqueue(id);
        _clock.Advance(1);
        queue.Enqueue(id);
        _clock.Advance(1);
        Assert.Equal(id, Assert.Single(queue.TakeReady(out _)).Key);
    }

    [Fact]
    public void ArrivalsWhileProcessingKeepTheirDeadlineAndAreNotLost()
    {
        var queue = new VmInitializationBatchQueue(_options, _clock);
        var first = Guid.NewGuid();
        queue.Enqueue(first);
        _clock.Advance(2);
        Assert.Equal(first, Assert.Single(queue.TakeReady(out _)).Key);

        var next = Guid.NewGuid();
        queue.Enqueue(next);
        _clock.Advance(10); // The previous batch is still processing.
        Assert.Equal(next, Assert.Single(queue.TakeReady(out _)).Key);
    }

    [Fact]
    public void ConfigurationChangesApplyToTheNextBatch()
    {
        var queue = new VmInitializationBatchQueue(_options, _clock);
        queue.Enqueue(Guid.NewGuid());
        _options.CurrentValue.Returns(new VmInitializationOptions { DebounceSeconds = 4, MaxWaitSeconds = 8 });
        _clock.Advance(2);
        Assert.Single(queue.TakeReady(out _));
        queue.Enqueue(Guid.NewGuid());
        _clock.Advance(2);
        Assert.Null(queue.TakeReady(out _));
        _clock.Advance(2);
        Assert.Single(queue.TakeReady(out _));
    }

    [Fact]
    public void RetriesAreBoundedAndCanJoinNewArrivals()
    {
        var queue = new VmInitializationBatchQueue(_options, _clock);
        var retryId = Guid.NewGuid();
        queue.Retry(retryId, 0);
        _clock.Advance(1);
        queue.Enqueue(Guid.NewGuid());
        _clock.Advance(3);
        var batch = queue.TakeReady(out _);
        Assert.Equal(2, batch.Count);
        Assert.Equal(1, batch[retryId]);

        foreach (var (attempt, seconds) in new[] { (1, 5), (2, 15) })
        {
            queue.Retry(retryId, attempt);
            _clock.Advance(seconds - 1);
            Assert.Null(queue.TakeReady(out _));
            _clock.Advance(3);
            Assert.Equal(attempt + 1, Assert.Single(queue.TakeReady(out _)).Value);
        }
        queue.Retry(retryId, 3);
        _clock.Advance(100);
        Assert.Null(queue.TakeReady(out var delay));
        Assert.Null(delay);
    }

    [Fact]
    public async Task ReadyWorkIsReadAndCancellationStopsBothIdleAndDebounceWaits()
    {
        var queue = new VmInitializationBatchQueue(_options, _clock);
        var id = Guid.NewGuid();
        queue.Enqueue(id);
        _clock.Advance(2);
        Assert.Equal(id, Assert.Single(await queue.ReadAsync(TestContext.Current.CancellationToken)).Key);

        using var idleCancel = new CancellationTokenSource();
        var idle = queue.ReadAsync(idleCancel.Token);
        await idleCancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle);

        queue.Enqueue(Guid.NewGuid());
        using var debounceCancel = new CancellationTokenSource();
        var debouncing = queue.ReadAsync(debounceCancel.Token);
        await debounceCancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => debouncing);
    }

    [Theory]
    [InlineData(VmType.Unknown, false, false, VmType.Vsphere)]
    [InlineData(VmType.Vsphere, false, false, VmType.Vsphere)]
    [InlineData(VmType.Unknown, true, false, VmType.Proxmox)]
    [InlineData(VmType.Unknown, false, true, null)]
    [InlineData(VmType.Azure, false, false, null)]
    public void ProviderRoutingSkipsExternalConsoles(VmType type, bool proxmox, bool console, VmType? expected)
    {
        var vm = new Domain.Models.Vm
        {
            Type = type,
            ProxmoxVmInfo = proxmox ? new ProxmoxVmInfo() : null,
            ConsoleConnectionInfo = console ? new ConsoleConnectionInfo() : null
        };
        Assert.Equal(expected, VmInitializationQueue.GetProvider(vm));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.Parse("2026-01-01T00:00:00Z").Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }
}
