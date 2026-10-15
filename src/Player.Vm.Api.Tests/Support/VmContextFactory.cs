// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api has two contexts: VmContext, which publishes entity events, and VmLoggingContext (the usage
// log), an ordinary DbContext with no interceptor and no event publishing, as Startup registers it.

using System;
using Crucible.Common.EntityEvents.Interceptors;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Player.Vm.Api.Data;

namespace Player.Vm.Api.Tests.Support;

/// <summary>Builds <see cref="VmContext"/> and <see cref="VmLoggingContext"/> instances wired the way production wires them.</summary>
/// <remarks>
/// <see cref="VmContext"/> extends <c>EventPublishingDbContext</c>, whose <c>PublishEventsAsync</c>
/// resolves <see cref="IMediator"/> and a logger off the settable <c>ServiceProvider</c> property with
/// <c>GetRequiredService</c>. Both must be registered or the first event-publishing save throws.
/// </remarks>
internal static class VmContextFactory
{
    /// <summary>
    /// The provider a session shares across its contexts, and the substituted mediator tests assert on.
    /// A substitute is right here: each session gets its own, and only its own test reads it.
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
    /// A context over the given provider configuration, with the entity event interceptor attached so
    /// SaveChanges publishes events exactly as it does in production.
    /// </summary>
    public static VmContext CreateContext(
        Action<DbContextOptionsBuilder<VmContext>> configureProvider,
        IServiceProvider services)
    {
        var builder = new DbContextOptionsBuilder<VmContext>();
        configureProvider(builder);

        // Production attaches this through AddEventPublishingDbContextFactory, which resolves it from the
        // container; its only dependency is a logger.
        builder.AddInterceptors(new EntityEventInterceptor(NullLogger<EntityEventInterceptor>.Instance));

        return new VmContext(builder.Options) { ServiceProvider = services };
    }

    /// <summary>
    /// A usage-log context over the given provider configuration. Plainer than <see cref="CreateContext"/>
    /// because the context is: production registers it with <c>AddDbContextPool</c> and nothing else, so
    /// it takes neither the interceptor nor a service provider.
    /// </summary>
    public static VmLoggingContext CreateLoggingContext(
        Action<DbContextOptionsBuilder<VmLoggingContext>> configureProvider,
        IServiceProvider services)
    {
        var builder = new DbContextOptionsBuilder<VmLoggingContext>();
        configureProvider(builder);

        return new VmLoggingContext(builder.Options);
    }
}
