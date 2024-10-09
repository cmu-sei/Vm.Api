// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Microsoft.EntityFrameworkCore;

namespace Player.Vm.Api.Data;

public class VmContextFactory : IDbContextFactory<VmContext>
{
    private readonly IDbContextFactory<VmContext> _pooledFactory;
    private readonly IServiceProvider _serviceProvider;

    public VmContextFactory(
        IDbContextFactory<VmContext> pooledFactory,
        IServiceProvider serviceProvider)
    {
        _pooledFactory = pooledFactory;
        _serviceProvider = serviceProvider;
    }

    public VmContext CreateDbContext()
    {
        var context = _pooledFactory.CreateDbContext();

        // Inject the current scope's ServiceProvider
        context.ServiceProvider = _serviceProvider;
        return context;
    }
}