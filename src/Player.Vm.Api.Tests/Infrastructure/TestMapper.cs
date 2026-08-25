// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// The application's real AutoMapper configuration, for tests that construct a service directly rather
/// than driving it over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Built the way <c>Startup</c> builds it - <c>AddAutoMapper(typeof(Startup))</c> over the whole
/// assembly - rather than by registering the profiles a test happens to need. A profile that stops
/// compiling or a resolver whose dependency goes unregistered then fails here too, instead of only in
/// the endpoint tests.
/// </para>
/// <para>
/// <c>ConsoleUrlOptions</c> has to be registered because <c>ConsoleUrlResolver</c> takes it as a
/// constructor dependency, so mapping any Vm would throw without it. The values are deliberately
/// recognizable: a test asserting on a console URL should be reading them from here, not matching a
/// production hostname that happened to be left in a config file.
/// </para>
/// <para>
/// Shared and static. <c>IMapper</c> is immutable and thread-safe once built, and building it scans the
/// assembly, which is not worth repeating per test.
/// </para>
/// </remarks>
internal static class TestMapper
{
    public static IMapper Value { get; } = Build();

    public const string DefaultUrl = "https://console.test.local/default";
    public const string VsphereUrl = "https://console.test.local/vsphere";
    public const string ProxmoxUrl = "https://console.test.local/proxmox";

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
