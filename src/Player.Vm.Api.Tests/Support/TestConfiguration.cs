// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api keeps no claims cache and reads no roles from the identity provider (its ClaimsTransformer only
// normalizes scope claims; permissions come from player.api), so the template's ClaimsTransformation
// keys do not apply. What varies per host (the databases, VmUsageLogging:Enabled, the scopes a token
// carries) is set by VmAppFactory itself.

using System.Collections.Generic;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// The configuration the app factory layers over the application's own <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// <c>WebApplicationFactory</c> resolves the content root to the API project directory, so the shipped
/// configuration is already in force and only keys whose shipped value breaks or weakens a test run belong
/// here. Every entry states which.
/// </remarks>
internal static class TestConfiguration
{
    /// <summary>The ISO size ceiling the host runs with, in bytes. See its entry below.</summary>
    public const long MaxIsoFileSize = 4096;

    /// <summary>
    /// The Proxmox cluster the host is configured against. Not only an address: it is the provider
    /// instance id every Proxmox <c>ViewNetwork</c> row is keyed on, so a test seeding one has to agree
    /// with it. Deliberately not the empty string <c>appsettings.json</c> ships, which is also
    /// <c>ViewNetwork.ProviderInstanceId</c>'s default and would let a row belonging to no cluster match.
    /// </summary>
    public const string ProxmoxHost = "pve.test";

    /// <summary>
    /// The scope behind the privileged policy, the value <c>appsettings.json</c> ships for
    /// <c>Authorization:PrivilegedScope</c>. <c>CallbacksEndpointTests</c>' service caller asks for it through <c>X-Test-Scope</c>.
    /// </summary>
    public const string PrivilegedScope = "player-vm-privileged";

    public static Dictionary<string, string> Values => new()
    {
        // The health-check UI needs a stylesheet from the content root and adds a poller of its own;
        // neither has anything to do with the API surface under test.
        ["HealthChecksUI:Enabled"] = "false",

        // Small enough that a test can exceed it without moving gigabytes. This one value is both the limit
        // UploadIso enforces and the multipart body limit Startup hands the form reader, so it is the only
        // way to reach either check over a real request.
        ["IsoUpload:MaxFileSize"] = MaxIsoFileSize.ToString(System.Globalization.CultureInfo.InvariantCulture),

        // Nothing dials it (IProxmoxService is substituted, so no PveClient is built), but the Proxmox
        // request path reads it as the provider instance id its view-network rows are keyed on.
        ["Proxmox:Host"] = ProxmoxHost,
    };
}
