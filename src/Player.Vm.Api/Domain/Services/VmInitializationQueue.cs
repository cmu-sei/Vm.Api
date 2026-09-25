// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Domain.Services;

public interface IVmInitializationQueue
{
    void Enqueue(Models.Vm vm);
}

public sealed class VmInitializationQueue(
    IOptionsMonitor<VmInitializationOptions> options,
    TimeProvider timeProvider) : IVmInitializationQueue
{
    internal VmInitializationBatchQueue Vsphere { get; } = new(options, timeProvider);
    internal VmInitializationBatchQueue Proxmox { get; } = new(options, timeProvider);

    public void Enqueue(Models.Vm vm)
    {
        var provider = GetProvider(vm);
        if (provider == VmType.Proxmox)
            Proxmox.Enqueue(vm.Id);
        else if (provider == VmType.Vsphere)
            Vsphere.Enqueue(vm.Id);
    }

    internal static VmType? GetProvider(Models.Vm vm)
    {
        if (vm.ProxmoxVmInfo != null)
            return VmType.Proxmox;
        if (vm.ConsoleConnectionInfo != null)
            return null;
        return vm.Type == VmType.Vsphere || (vm.Type == VmType.Unknown && vm.DefaultUrl)
            ? VmType.Vsphere : null;
    }
}

/// <summary>
/// One provider's pending work. Scheduling is separate from processing so arrivals retain their
/// deadlines while the provider is busy. A signal only wakes the reader; IDs live in the dictionary.
/// </summary>
internal sealed class VmInitializationBatchQueue(
    IOptionsMonitor<VmInitializationOptions> options,
    TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _pending = new();
    private readonly Dictionary<Guid, (int Attempt, DateTimeOffset Due)> _retries = new();
    private readonly Channel<byte> _changed = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private static readonly int[] RetrySeconds = [2, 5, 15];
    private DateTimeOffset _first;
    private DateTimeOffset _last;
    private TimeSpan _debounce;
    private TimeSpan _maxWait;

    public void Enqueue(Guid id)
    {
        lock (_gate)
        {
            _retries.Remove(id);
            AddPending(id, 0, timeProvider.GetUtcNow());
        }
        _changed.Writer.TryWrite(0);
    }

    public void Retry(Guid id, int attempt)
    {
        if (attempt >= RetrySeconds.Length)
            return;

        lock (_gate)
        {
            if (!_pending.ContainsKey(id))
                _retries[id] = (attempt + 1, timeProvider.GetUtcNow().AddSeconds(RetrySeconds[attempt]));
        }
        _changed.Writer.TryWrite(0);
    }

    private void AddPending(Guid id, int attempt, DateTimeOffset arrival)
    {
        if (_pending.ContainsKey(id))
            return;

        if (_pending.Count == 0)
        {
            var settings = options.CurrentValue;
            _debounce = TimeSpan.FromSeconds(settings.DebounceSeconds);
            _maxWait = TimeSpan.FromSeconds(settings.MaxWaitSeconds);
            _first = _last = arrival;
        }
        else
        {
            if (arrival < _first) _first = arrival;
            if (arrival > _last) _last = arrival;
        }
        _pending.Add(id, attempt);
    }

    // Also used by deterministic tests: advancing a clock never requires sleeping the test thread.
    internal Dictionary<Guid, int> TakeReady(out TimeSpan? delay)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var (id, retry) in _retries.Where(x => x.Value.Due <= now).OrderBy(x => x.Value.Due).ToArray())
            {
                AddPending(id, retry.Attempt, retry.Due);
                _retries.Remove(id);
            }

            DateTimeOffset? deadline = null;
            if (_pending.Count > 0)
            {
                deadline = new[] { _first + _maxWait, _last + _debounce }.Min();
                if (deadline <= now)
                {
                    var batch = new Dictionary<Guid, int>(_pending);
                    _pending.Clear();
                    delay = TimeSpan.Zero;
                    return batch;
                }
            }

            foreach (var retry in _retries.Values)
                if (deadline == null || retry.Due < deadline)
                    deadline = retry.Due;

            delay = deadline - now;
            return null;
        }
    }

    public async Task<Dictionary<Guid, int>> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var ready = TakeReady(out var delay);
            if (ready != null)
                return ready;

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var signal = _changed.Reader.ReadAsync(wait.Token).AsTask();
            if (delay == null)
            {
                await signal;
                continue;
            }

            var timer = Task.Delay(delay.Value, timeProvider, wait.Token);
            await Task.WhenAny(signal, timer);
            await wait.CancelAsync();
            try { await Task.WhenAll(signal, timer); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }
}
