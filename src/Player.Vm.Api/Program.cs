// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Player.Vm.Api.Infrastructure.Extensions;

namespace Player.Vm.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateWebHostBuilder(args)
                .Build()
                .InitializeDatabase()
                .Run();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args)
        {
            var configuration = new ConfigurationBuilder().AddCommandLine(args).Build();

            return WebHost.CreateDefaultBuilder(args)
                .UseConfiguration(configuration)
                .UseStartup<Startup>();
        }
    }
}
