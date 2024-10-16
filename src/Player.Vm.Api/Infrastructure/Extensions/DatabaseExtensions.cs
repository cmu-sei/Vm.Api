// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using Microsoft.AspNetCore.Hosting;
using Player.Vm.Api.Data;
using Player.Vm.Api.Infrastructure.Options;
using Microsoft.Extensions.Hosting;

namespace Player.Vm.Api.Infrastructure.Extensions
{
    public static class DatabaseExtensions
    {
        public static IHost InitializeDatabase(this IHost webHost)
        {
            using (var scope = webHost.Services.CreateScope())
            {
                var services = scope.ServiceProvider;

                try
                {
                    var databaseOptions = services.GetService<DatabaseOptions>();
                    var vmCtx = services.GetRequiredService<VmContext>();
                    var loggingOptions = services.GetService<VmUsageLoggingOptions>();

                    if (vmCtx != null)
                    {
                        if (databaseOptions.DevModeRecreate)
                            vmCtx.Database.EnsureDeleted();

                        // Do not run migrations on Sqlite, only devModeRecreate allowed
                        if (!vmCtx.Database.IsSqlite())
                        {
                            vmCtx.Database.Migrate();
                        }

                        if (databaseOptions.DevModeRecreate)
                        {
                            vmCtx.Database.EnsureCreated();
                        }
                    }

                    if (loggingOptions.Enabled)
                    {
                        var vmCtxLogging = services.GetRequiredService<VmLoggingContext>();

                        if (databaseOptions.DevModeRecreate)
                            vmCtxLogging.Database.EnsureDeleted();

                        // Do not run migrations on Sqlite, only devModeRecreate allowed
                        if (!vmCtxLogging.Database.IsSqlite())
                        {
                            vmCtxLogging.Database.Migrate();
                        }

                        if (databaseOptions.DevModeRecreate)
                        {
                            vmCtxLogging.Database.EnsureCreated();
                        }
                    }

                }
                catch (Exception ex)
                {
                    var logger = services.GetRequiredService<ILogger<Program>>();
                    logger.LogError(ex, "An error occurred while initializing the database.");

                    // exit on database connection error on startup so app can be restarted to try again
                    throw;
                }
            }

            return webHost;
        }

        public static DbContextOptionsBuilder UseConfiguredDatabase(
            this DbContextOptionsBuilder builder,
            IConfiguration config
        )
        {
            string dbProvider = config.GetValue<string>("Database:Provider", "Sqlite").Trim();
            var connectionString = config.GetConnectionString(dbProvider);

            switch (dbProvider)
            {
                case "Sqlite":
                    builder.UseSqlite(connectionString);
                    break;

                case "SqlServer":
                    builder.UseSqlServer(connectionString);
                    break;

                case "PostgreSQL":
                    builder.UseNpgsql(connectionString);
                    break;

            }
            return builder;
        }
    }
}
