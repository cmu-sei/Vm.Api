// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Text.RegularExpressions;
using Player.Vm.Api.Features.Files;

namespace Player.Vm.Api.Domain.Proxmox
{
    // Encodes View/team ISO scoping into the filename, because Proxmox ISO storage is flat.
    //
    // vSphere scopes ISOs with a datastore folder hierarchy ({baseFolder}/{viewId}/{scopeId}), but a
    // Proxmox storage keeps every ISO in one template/iso directory and '/' is not legal in a
    // filename, so there is no folder dimension to scope on. TopoMojo solved this by folding the
    // scope GUID into the name with '#'; we use the same trick with three segments rather than two:
    //
    //     {viewId}__{scopeId}__{displayName}.iso
    //
    // Three segments mean a delete can rebuild the exact filename from (viewId, scopeId, filename)
    // with no listing first, a View-scoped list is a prefix match, and the names cannot be confused
    // with TopoMojo's 2-segment ones if both products share a storage.
    //
    // The separator is a parameter rather than a constant: it comes from
    // ProxmoxOptions.IsoScopeSeparator so that a change to how PVE normalizes uploaded filenames is
    // a config fix instead of a code change. It defaults to '__' rather than TopoMojo's '#' because
    // PVE's storage upload API rewrites '#' - see Normalize below.
    public static class ProxmoxIsoNaming
    {
        public const int SegmentCount = 3;

        // Characters PVE's storage upload API leaves alone. Everything else it rewrites to '_', so a
        // name pushed through that API comes back changed unless it is already within this set. Both
        // write modes normalize, not just the API one, so switching UploadToStorage does not orphan
        // the files already written under the other mode's naming.
        private static readonly Regex Disallowed = new(@"[^-a-zA-Z0-9_.]", RegexOptions.Compiled);

        private static readonly Regex UnderscoreRun = new(@"_{2,}", RegexOptions.Compiled);

        // Fold a display filename into the set above, exactly as PVE would, then collapse runs of '_'
        // to one. The collapse is what makes '__' safe to use as the scope separator: without it,
        // "Win 10 (x64).iso" would normalize to "Win_10__x64_.iso" and grow a separator of its own,
        // which TryDecode would then read as five segments.
        //
        // Idempotent, because IsoService applies one normalizer per enabled provider.
        public static string Normalize(string filename)
        {
            if (string.IsNullOrEmpty(filename))
                return filename;

            return UnderscoreRun.Replace(Disallowed.Replace(filename, "_"), "_");
        }

        // Whether PVE's upload API would leave a string alone. Distinct from Normalize, which also
        // collapses runs of '_': the separator itself is '__', so it is deliberately NOT run-collapse
        // safe and must be checked against the charset rule only.
        public static bool SurvivesUpload(string value)
        {
            return !string.IsNullOrEmpty(value) && !Disallowed.IsMatch(value);
        }

        // Longest filename most storage backends accept, in bytes. The two GUIDs and two separators
        // cost 74 characters of that budget, so a name that uploads fine to vSphere can still be too
        // long here - callers validate before writing anything anywhere.
        public const int MaxEncodedLength = 255;

        public static string Encode(Guid viewId, string scopeId, string filename, string separator)
        {
            return string.Concat(viewId.ToString(), separator, scopeId, separator, filename);
        }

        // Parses an encoded ISO filename back into its scope. Deliberately strict: anything that is
        // not exactly three separator-delimited parts with two parseable GUIDs and an .iso extension
        // is rejected, so hand-placed ISOs, PVE's own templates, and TopoMojo's 2-segment names are
        // skipped rather than surfaced under some arbitrary View.
        public static bool TryDecode(
            string volumeFileName,
            string separator,
            out Guid viewId,
            out Guid scopeId,
            out string displayName)
        {
            viewId = Guid.Empty;
            scopeId = Guid.Empty;
            displayName = null;

            if (string.IsNullOrEmpty(volumeFileName) || string.IsNullOrEmpty(separator))
                return false;

            var parts = volumeFileName.Split(separator);

            if (parts.Length != SegmentCount)
                return false;

            if (!Guid.TryParse(parts[0], out viewId) || !Guid.TryParse(parts[1], out scopeId))
            {
                viewId = Guid.Empty;
                scopeId = Guid.Empty;
                return false;
            }

            if (parts[2].Length == 0 || !IsoFileNaming.IsIsoFile(parts[2]))
            {
                viewId = Guid.Empty;
                scopeId = Guid.Empty;
                return false;
            }

            displayName = parts[2];
            return true;
        }

        // The filename portion of a PVE volume id. PVE reports both "storage:iso/name.iso" and
        // "storage:/iso/name.iso" depending on the storage type, and the filename never contains a
        // '/', so taking the last segment covers both. Matches TopoMojo's PveIso.Name.
        public static string VolumeFileName(string volumeId)
        {
            if (string.IsNullOrEmpty(volumeId))
                return volumeId;

            var index = volumeId.LastIndexOf('/');
            return index < 0 ? volumeId : volumeId[(index + 1)..];
        }

        public static string BuildVolumeId(string storage, string encodedFileName)
        {
            return $"{storage}:iso/{encodedFileName}";
        }
    }
}
