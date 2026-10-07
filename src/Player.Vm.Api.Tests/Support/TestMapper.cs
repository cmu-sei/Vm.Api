// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api's profiles use a value resolver (ConsoleUrlResolver) that takes ConsoleUrlOptions from the
// container, so the mapper is built through DI as Startup's AddAutoMapper(typeof(Startup)) builds it,
// with recognizable console urls registered.

using AutoMapper;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Tests.Support;

/// <summary>The application's real AutoMapper configuration, built without starting the application.</summary>
/// <remarks>
/// Built over the whole assembly rather than from the profiles a test needs, so a profile that stops
/// compiling or a resolver whose dependency goes unregistered fails here too. The console urls are
/// deliberately recognizable: a test asserting on one reads it from here, not from a config file.
/// </remarks>
public static class TestMapper
{
    public const string DefaultUrl = "https://console.test.local/default";
    public const string VsphereUrl = "https://console.test.local/vsphere";
    public const string ProxmoxUrl = "https://console.test.local/proxmox";

    private static readonly System.Lazy<IMapper> LazyMapper = new(Build);

    /// <summary>The shared configuration, built once for the run.</summary>
    public static IConfigurationProvider Configuration => Mapper.ConfigurationProvider;

    /// <summary>A mapper over <see cref="Configuration"/>. Thread-safe; tests share one.</summary>
    public static IMapper Mapper => LazyMapper.Value;

    private static IMapper Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton(new ConsoleUrlOptions
        {
            DefaultUrl = DefaultUrl,
            Vsphere = new VsphereConsoleUrlOptions { Url = VsphereUrl },
            Proxmox = new ProxmoxConsoleUrlOptions { Url = ProxmoxUrl },
            Guacamole = new GuacamoleConsoleUrlOptions { ProviderName = "guacamole" }
        });

        services.AddAutoMapper(typeof(Startup));

        return services.BuildServiceProvider().GetRequiredService<IMapper>();
    }
}
