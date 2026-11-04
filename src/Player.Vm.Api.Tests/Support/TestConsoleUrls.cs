// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// Console urls for the services and handlers a test builds itself, which map a Vm to its view model
/// and so need to know where its console is opened.
/// </summary>
/// <remarks>The urls are deliberately recognizable: a test asserting on one reads it from here, not from a config file.</remarks>
public static class TestConsoleUrls
{
    public const string DefaultUrl = "https://console.test.local/default";
    public const string VsphereUrl = "https://console.test.local/vsphere";
    public const string ProxmoxUrl = "https://console.test.local/proxmox";

    public static ConsoleUrlOptions Options { get; } = new()
    {
        DefaultUrl = DefaultUrl,
        Vsphere = new VsphereConsoleUrlOptions { Url = VsphereUrl },
        Proxmox = new ProxmoxConsoleUrlOptions { Url = ProxmoxUrl },
        Guacamole = new GuacamoleConsoleUrlOptions { ProviderName = "guacamole" }
    };
}
