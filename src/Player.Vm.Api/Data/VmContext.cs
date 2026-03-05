// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crucible.Common.EntityEvents;
using Crucible.Common.EntityEvents.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Extensions;

namespace Player.Vm.Api.Data
{
    [GenerateEntityEventInterfaces(typeof(INotification))]
    public class VmContext : EventPublishingDbContext
    {
        public VmContext(DbContextOptions options)
            : base(options) { }

        public DbSet<Domain.Models.Vm> Vms { get; set; }
        public DbSet<VmTeam> VmTeams { get; set; }
        public DbSet<VmMap> Maps { get; set; }
        public DbSet<Player.Api.Client.WebhookEvent> WebhookEvents { get; set; }
        public DbSet<ProxmoxVmInfo> ProxmoxVmInfo { get; set; }
        public DbSet<VmUser> VmUsers { get; set; }
        public DbSet<ViewNetwork> ViewNetworks { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurations();

            // Apply PostgreSQL specific options
            if (Database.IsNpgsql())
            {
                modelBuilder.AddPostgresUUIDGeneration();
                modelBuilder.UsePostgresCasing();
            }
        }

        public override async Task PublishEventsAsync(IReadOnlyList<IEntityEvent> events, CancellationToken cancellationToken)
        {
            if (ServiceProvider is not null)
            {
                var mediator = ServiceProvider.GetRequiredService<IMediator>();
                var logger = ServiceProvider.GetRequiredService<ILogger<VmContext>>();

                foreach (var evt in events.Cast<INotification>())
                {
                    try
                    {
                        await mediator.Publish(evt, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error publishing entity event {EventType}", evt.GetType().Name);
                    }
                }
            }
        }
    }
}
