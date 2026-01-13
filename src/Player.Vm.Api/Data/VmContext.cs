// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Extensions;

namespace Player.Vm.Api.Data
{
    public class VmContext : DbContext
    {
        // Needed for EventInterceptor
        public IServiceProvider ServiceProvider;

        // Entity Events collected by EventTransactionInterceptor and published in SaveChanges
        public List<INotification> Events { get; } = [];

        public VmContext(DbContextOptions options)
            : base(options) { }

        public DbSet<Domain.Models.Vm> Vms { get; set; }
        public DbSet<VmTeam> VmTeams { get; set; }
        public DbSet<VmMap> Maps { get; set; }
        public DbSet<Player.Api.Client.WebhookEvent> WebhookEvents { get; set; }
        public DbSet<ProxmoxVmInfo> ProxmoxVmInfo { get; set; }
        public DbSet<VmUser> VmUsers { get; set; }

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

        public override int SaveChanges()
        {
            var result = base.SaveChanges();
            PublishEvents().Wait();
            return result;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var result = await base.SaveChangesAsync(cancellationToken);
            await PublishEvents(cancellationToken);
            return result;
        }

        private async Task PublishEvents(CancellationToken cancellationToken = default)
        {
            // Publish deferred events after transaction is committed and cleared
            if (Events.Count > 0 && ServiceProvider is not null)
            {
                var mediator = ServiceProvider.GetRequiredService<IMediator>();
                var eventsToPublish = Events.ToArray();
                Events.Clear();

                foreach (var evt in eventsToPublish)
                {
                    await mediator.Publish(evt, cancellationToken);
                }
            }
        }
    }
}
