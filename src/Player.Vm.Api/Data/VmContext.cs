// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Extensions;

namespace Player.Vm.Api.Data
{
    public class VmContext : DbContext
    {
        private DbContextOptions _options;

        public VmContext(DbContextOptions options)
            : base(options)
        {
            _options = options;
        }

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
