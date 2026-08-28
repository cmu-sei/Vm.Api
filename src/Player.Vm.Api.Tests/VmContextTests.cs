// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using NSubstitute;
using Player.Vm.Api.Data;
using Xunit;

namespace Player.Vm.Api.Tests;

public class VmContextTests
{
    [Fact]
    public async Task PublishEventsAsync_WhenServiceProviderIsDisposed_DoesNotThrow()
    {
        var serviceProvider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IMediator>())
            .BuildServiceProvider();

        await using var context = new VmContext(
            new DbContextOptionsBuilder<VmContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options)
        {
            ServiceProvider = serviceProvider
        };

        serviceProvider.Dispose();

        await context.PublishEventsAsync([], CancellationToken.None);
    }
}
