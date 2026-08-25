// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Crucible.Common.EntityEvents.Interceptors;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Player.Vm.Api.Data;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// Builds <see cref="VmContext"/> instances wired the way production wires them.
/// </summary>
/// <remarks>
/// <see cref="VmContext"/> extends <c>EventPublishingDbContext</c>, and its
/// <c>PublishEventsAsync</c> resolves both <see cref="IMediator"/> and <c>ILogger&lt;VmContext&gt;</c>
/// off the settable <c>ServiceProvider</c> property with <c>GetRequiredService</c>. Both have to be
/// registered or the first save that publishes an event throws.
/// </remarks>
internal static class VmContextFactory
{
    /// <summary>
    /// The service provider a session shares across all of its contexts, along with the substituted
    /// mediator tests assert entity events on.
    /// </summary>
    public static (IServiceProvider Services, IMediator Mediator) CreateServices()
    {
        var mediator = Substitute.For<IMediator>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mediator);

        return (services.BuildServiceProvider(), mediator);
    }

    /// <summary>
    /// A context for the given connection string, with the entity event interceptor attached so
    /// SaveChanges publishes events exactly as it does in production.
    /// </summary>
    public static VmContext CreateContext(string connectionString, IServiceProvider services)
    {
        var builder = new DbContextOptionsBuilder<VmContext>()
            .UseNpgsql(connectionString);

        // Production attaches this through AddEventPublishingDbContextFactory, which resolves it from
        // the container. Constructed directly here: these contexts are built outside the application's
        // provider, and the interceptor's only dependency is a logger.
        builder.AddInterceptors(new EntityEventInterceptor(NullLogger<EntityEventInterceptor>.Instance));

        return new VmContext(builder.Options) { ServiceProvider = services };
    }
}
