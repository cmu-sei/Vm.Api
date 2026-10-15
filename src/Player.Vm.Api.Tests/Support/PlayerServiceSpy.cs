// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Player.Vm.Api.Domain.Services;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// The real <see cref="PlayerService"/> behind an <see cref="IPlayerService"/> that records each call
/// before passing it on. For the few service tests whose subject is how a service asks (one batched
/// question rather than one per team), not what it is told: every answer is still the real service's.
/// </summary>
public class PlayerServiceSpy : DispatchProxy
{
    private readonly ConcurrentQueue<(string Method, object[] Args)> _calls = new();
    private IPlayerService _inner;

    /// <summary>Every call, in order: the member name and its arguments.</summary>
    public IReadOnlyList<(string Method, object[] Args)> Calls => [.. _calls];

    /// <summary>The calls to <paramref name="method"/>.</summary>
    public IReadOnlyList<object[]> CallsTo(string method) =>
        [.. _calls.Where(x => x.Method == method).Select(x => x.Args)];

    /// <summary>Wraps <paramref name="inner"/>; the returned object is both the service and its spy.</summary>
    public static IPlayerService Wrap(IPlayerService inner, out PlayerServiceSpy spy)
    {
        var proxy = Create<IPlayerService, PlayerServiceSpy>();
        spy = (PlayerServiceSpy)(object)proxy;
        spy._inner = inner;

        return proxy;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        _calls.Enqueue((targetMethod.Name, args));

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
