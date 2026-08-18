// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Vsphere.Options;

namespace Player.Vm.Api.Features.Files;

/// <summary>
/// Reports ISO configuration that will not do what its author intended, once, at startup.
/// </summary>
/// <remarks>
/// The vSphere upload settings moved out of the shared <c>IsoUpload</c> block into <c>Vsphere:</c>, and
/// a legacy key is now simply ignored rather than mapped forward - there is no way to tell an
/// operator's choice from the value the old appsettings.json and helm chart both shipped. So an
/// un-migrated deployment would otherwise boot cleanly and fail only on its first upload, which is why
/// this says so up front.
/// <para>
/// Deliberately logs and returns rather than throwing: a misconfigured ISO destination breaks ISO
/// upload, not the API, and refusing to boot over it would take console access and Vm power control
/// down with it.
/// </para>
/// </remarks>
internal static class IsoOptionsCheck
{
    private const string NewIsoRoot = "Vsphere:IsoRoot";
    private const string NewIsoUploadViaApi = "Vsphere:IsoUploadViaApi";

    public static void Log(IConfiguration configuration, ILogger logger)
    {
        CheckMovedKey(configuration, logger, "IsoUpload:BasePath", NewIsoRoot, "IsoUpload__BasePath", "Vsphere__IsoRoot");
        CheckMovedKey(configuration, logger, "IsoUpload:UploadToDatastore", NewIsoUploadViaApi, "IsoUpload__UploadToDatastore", "Vsphere__IsoUploadViaApi");

        // No check for Proxmox:UploadToStorage: Proxmox ISO support is unreleased, so no deployment can
        // have set it and its rename to Proxmox:IsoUploadViaApi is invisible from outside this branch.
        CheckIneffectiveVsphereSettings(configuration, logger);
    }

    // Error when the replacement is unset, because ISO upload is then broken; warning when both are
    // present and agree, because that is the correct end state with tidying left to do - the transitional
    // shape a deployment sits in while its environment variables are being renamed.
    private static void CheckMovedKey(
        IConfiguration configuration,
        ILogger logger,
        string oldKey,
        string newKey,
        string oldEnvVar,
        string newEnvVar)
    {
        if (!configuration.GetSection(oldKey).Exists())
            return;

        if (HasValue(configuration, newKey))
        {
            // Both set is only reassuring if they agree. The replacement keys all ship with values in
            // appsettings.json, so "the new key is set" on its own would say nothing about whether the
            // operator's own choice survived - an un-migrated deployment that only overrode the legacy
            // key silently runs on the shipped default instead. A disagreement is the one signal in
            // reach that distinguishes the two, so it gets the error rather than the tidy-up note.
            if (!ValuesAgree(configuration, oldKey, newKey))
            {
                logger.LogError(
                    "ISO configuration '{OldKey}' has moved to '{NewKey}' and is now IGNORED. '{OldKey}' is '{OldValue}' but the effective '{NewKey}' is '{NewValue}', so ISO uploads are NOT using the value this deployment set. Set {NewEnvVar} to the intended value and remove {OldEnvVar}.",
                    oldKey, newKey, oldKey, configuration[oldKey], newKey, configuration[newKey], newEnvVar, oldEnvVar);
                return;
            }

            logger.LogWarning(
                "ISO configuration '{OldKey}' has moved to '{NewKey}' and is now ignored. '{NewKey}' is set to the same value, so behavior is correct - remove '{OldEnvVar}' from this deployment's configuration.",
                oldKey, newKey, newKey, oldEnvVar);
            return;
        }

        logger.LogError(
            "ISO configuration '{OldKey}' has moved to '{NewKey}' and is now IGNORED, and '{NewKey}' is not set. ISO uploads will fail until this deployment sets {NewEnvVar} (previously {OldEnvVar}).",
            oldKey, newKey, newKey, newEnvVar, oldEnvVar);
    }

    // A vSphere ISO destination on a deployment with no vSphere host does nothing. Worth saying,
    // because it usually means the value landed under the wrong provider's section.
    private static void CheckIneffectiveVsphereSettings(IConfiguration configuration, ILogger logger)
    {
        if (!HasValue(configuration, NewIsoRoot) && !HasValue(configuration, NewIsoUploadViaApi))
            return;

        var vsphere = new VsphereOptions();
        configuration.GetSection("Vsphere").Bind(vsphere);

        if (vsphere.Hosts?.Any(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Address)) == true)
            return;

        logger.LogWarning(
            "'{IsoRoot}' / '{IsoUploadViaApi}' are set but no enabled vSphere host has an address, so they have no effect. Proxmox ISO storage is configured with 'Proxmox:IsoRoot' and 'Proxmox:IsoUploadViaApi'.",
            NewIsoRoot, NewIsoUploadViaApi);
    }

    // Trimmed and case-insensitive because a moved key's two names are the same setting spelled twice:
    // "True" against "true" is the same choice, and no ISO destination is distinguished only by case.
    private static bool ValuesAgree(IConfiguration configuration, string oldKey, string newKey) =>
        string.Equals(
            configuration[oldKey]?.Trim(),
            configuration[newKey]?.Trim(),
            StringComparison.OrdinalIgnoreCase);

    // Present-but-empty counts as unset: an environment-variable deployment cannot remove a key, only
    // blank it, so a blank is how it says "not configured".
    private static bool HasValue(IConfiguration configuration, string key) =>
        !string.IsNullOrWhiteSpace(configuration[key]);
}
