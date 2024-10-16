// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Extensions;

namespace Player.Vm.Api.Data
{
    public class VmContext : DbContext
    {
        // Needed for EventInterceptor
        public IServiceProvider ServiceProvider;

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
    }
}
