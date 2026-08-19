// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Iso9660;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Infrastructure.Options;

// Aliased rather than imported wholesale: Domain.Models also defines Team and View, which would
// collide with the Player.Api.Client types this file's ViewTeams record is built from.
using VmType = Player.Vm.Api.Domain.Models.VmType;

namespace Player.Vm.Api.Features.Files
{
    // A View and the teams within it to render an ISO listing for.
    public record ViewTeams(View View, IReadOnlyCollection<Team> Teams);

    // Shared ISO logic used by more than one of the Files request handlers: permission/scope
    // resolution for upload and delete, the write orchestration across every enabled hypervisor,
    // assembling the per-View listing, plus the pure filename/teamId parsing helpers. Per-endpoint
    // request shaping lives in the Requests/* handlers.
    public interface IIsoService
    {
        Task<IReadOnlyList<string>> ResolveUploadScopeIdsAsync(Guid viewId, string scope, IReadOnlyList<Guid> teamIds, CancellationToken ct);
        Task<string> ResolveDeleteScopeIdAsync(Guid viewId, string scope, Guid? teamId, CancellationToken ct);

        // Writes the upload to every enabled provider, for every resolved scope. openUpload returns the
        // raw request body; whether it is consumed directly or staged to disk first is decided here.
        Task<IsoUploadResult> UploadAsync(Guid viewId, IReadOnlyList<string> scopeIds, string filename, Func<Stream> openUpload, CancellationToken ct);

        Task<IsoUploadResult> DeleteAsync(Guid viewId, string scopeId, string filename, CancellationToken ct);

        Task<ManagedIsoResult[]> BuildViewIsoResultsAsync(IReadOnlyCollection<ViewTeams> views, CancellationToken ct);
        Task<MountableIsoResult[]> BuildVmIsoResultsAsync(Guid vmId, VmType vmType, IReadOnlyCollection<ViewTeams> views, CancellationToken ct);

        // The Views a VM's teams place it in, paired with the teams whose ISOs may be mounted on that VM.
        // Shared by every provider's per-VM ISO query so the picker and ResolveMountValueAsync agree.
        Task<IReadOnlyList<ViewTeams>> ResolveViewTeamsForVmAsync(IEnumerable<Guid> teamIds, CancellationToken ct);

        // Authorize a client-submitted mount value against the scope encoded in it and return the
        // canonical token to mount. Throws ForbiddenException if the value is not a Player-managed ISO
        // in a scope this VM and this caller may both use.
        Task<string> ResolveMountValueAsync(Guid vmId, VmType vmType, IEnumerable<Guid> vmTeamIds, string mountValue, CancellationToken ct);

        string SanitizeFilename(string filename);
        IReadOnlyList<Guid> ParseTeamIds(StringValues values);
    }

    public class IsoService : IIsoService
    {
        private readonly IPlayerService _playerService;
        private readonly IViewService _viewService;
        private readonly IReadOnlyList<IIsoProvider> _providers;
        private readonly IsoUploadOptions _isoUploadOptions;
        private readonly ILogger<IsoService> _logger;

        public IsoService(
            IPlayerService playerService,
            IViewService viewService,
            IEnumerable<IIsoProvider> providers,
            IsoUploadOptions isoUploadOptions,
            ILogger<IsoService> logger)
        {
            _playerService = playerService;
            _viewService = viewService;
            _providers = providers.ToList();
            _isoUploadOptions = isoUploadOptions;
            _logger = logger;
        }

        // The providers an operation actually targets. A provider that is not configured for ISOs is
        // invisible rather than an error, so an install with only vSphere set up behaves exactly as it
        // did before there was more than one provider.
        //
        // Computed once per instance rather than per read: this is a scoped service and every provider's
        // Enabled reads a per-request options snapshot, so it cannot change part-way through a request.
        private IReadOnlyList<IIsoProvider> EnabledProviders =>
            _enabledProviders ??= _providers.Where(p => p.Enabled).ToList();

        private IReadOnlyList<IIsoProvider> _enabledProviders;

        // Enforces the ISO UPLOAD permissions and returns the scopeId(s) the ISO folder(s) are keyed on:
        //  - "view" scope: requires UploadViewIsos; a single scopeId of the view id.
        //  - "team" scope: targets are the given teamIds (each validated to belong to the View) or the
        //    primary team when none are supplied. Each target requires UploadViewIsos (any team) or
        //    UploadTeamIsos on that team. scopeIds are the team ids.
        public async Task<IReadOnlyList<string>> ResolveUploadScopeIdsAsync(Guid viewId, string scope, IReadOnlyList<Guid> teamIds, CancellationToken ct)
        {
            if (scope == "view")
            {
                if (!await _playerService.Can([], [viewId], [], [AppViewPermission.UploadViewIsos], [], ct))
                    throw new ForbiddenException("You do not have permission to upload public files for this View");

                return new[] { viewId.ToString() };
            }

            // Resolve the target team(s): explicit teamIds (each must belong to the View) else the primary team.
            List<Guid> targetTeamIds;
            if (teamIds.Count > 0)
            {
                foreach (var teamId in teamIds)
                {
                    if (!await _playerService.IsTeamInViewAsync(teamId, viewId, ct))
                        throw new BadRequestException("The specified team is not part of this View");
                }
                targetTeamIds = teamIds.ToList();
            }
            else
            {
                targetTeamIds = new List<Guid> { await GetPrimaryTeamIdOrThrowAsync(viewId, ct) };
            }

            // UploadViewIsos lets a view-admin upload to any team; otherwise UploadTeamIsos on the
            // target team is required. Checked per team so a partially-permitted selection is rejected.
            foreach (var targetTeamId in targetTeamIds)
            {
                if (!await _playerService.Can([targetTeamId], [viewId], [], [AppViewPermission.UploadViewIsos], [AppTeamPermission.UploadTeamIsos], ct))
                    throw new ForbiddenException("You do not have permission to upload files for this Team");
            }

            return targetTeamIds.Select(id => id.ToString()).ToList();
        }

        // Enforces the ISO DELETE permissions and returns the scopeId to delete from.
        //  - "view" scope: requires DeleteViewIsos; scopeId is the view id.
        //  - "team" scope: target team is teamId (validated to belong to the View) or the primary team
        //    when teamId is absent. Allowed if the caller has DeleteViewIsos (any team) or DeleteTeamIsos
        //    on that team. scopeId is the team id.
        // The system-level DeleteIsos permission additionally authorizes deleting an ISO in ANY
        // View/team - including ones the caller is not a member of (the "all views" management mode).
        public async Task<string> ResolveDeleteScopeIdAsync(Guid viewId, string scope, Guid? teamId, CancellationToken ct)
        {
            // DeleteIsos is the only permission that lets a caller delete an ISO they have no specific
            // Delete*Isos permission for; checked up front so it can short-circuit the per-scope checks.
            var hasSystemDeleteIsos = await _playerService.Can([], [], [AppSystemPermission.DeleteIsos], [], [], ct);

            if (scope == "view")
            {
                if (hasSystemDeleteIsos)
                    return viewId.ToString();

                if (!await _playerService.Can([], [viewId], [], [AppViewPermission.DeleteViewIsos], [], ct))
                    throw new ForbiddenException("You do not have permission to delete public files for this View");

                return viewId.ToString();
            }

            // Resolve the target team: an explicit teamId (must belong to the View) else the primary team.
            Guid targetTeamId;
            if (teamId.HasValue)
            {
                if (!await _playerService.IsTeamInViewAsync(teamId.Value, viewId, ct))
                    throw new BadRequestException("The specified team is not part of this View");

                targetTeamId = teamId.Value;
            }
            else
            {
                targetTeamId = await GetPrimaryTeamIdOrThrowAsync(viewId, ct);
            }

            if (hasSystemDeleteIsos)
                return targetTeamId.ToString();

            // DeleteViewIsos lets a view-admin delete any team's ISO; otherwise DeleteTeamIsos on the
            // target team is required.
            if (!await _playerService.Can([targetTeamId], [viewId], [], [AppViewPermission.DeleteViewIsos], [AppTeamPermission.DeleteTeamIsos], ct))
                throw new ForbiddenException("You do not have permission to delete files for this Team");

            return targetTeamId.ToString();
        }

        // Writes an uploaded file, as an ISO, to every enabled provider and every resolved scope.
        //
        // The ISO namespace is View/team-scoped rather than VM-scoped: a View routinely holds both
        // vSphere and Proxmox VMs, and whoever uploads a file picks a file, not a hypervisor. So the
        // write fans out, and a provider that fails is reported in the counts rather than failing the
        // request - the Files tab then shows the file as missing there, and re-uploading heals it.
        public async Task<IsoUploadResult> UploadAsync(
            Guid viewId, IReadOnlyList<string> scopeIds, string filename, Func<Stream> openUpload, CancellationToken ct)
        {
            var providers = EnabledProviders;

            if (providers.Count == 0)
                throw new BadRequestException("No hypervisor is configured to store ISOs.");

            // A real ISO keeps its name; anything else is wrapped into an ISO and gains the extension.
            var isIso = IsoFileNaming.IsIsoFile(filename);
            var destName = NormalizeFilename(providers, isIso ? filename : filename + IsoFileNaming.Extension);

            // Every provider vets the name before anything is written anywhere, so a name that is
            // illegal on one hypervisor fails the whole upload with a 400 instead of landing on some
            // providers and not others.
            foreach (var provider in providers)
            {
                foreach (var scopeId in scopeIds)
                {
                    provider.ValidateFilename(viewId, scopeId, destName);
                }
            }

            // The request body can only be read once. It can be handed straight to a provider only when
            // exactly one provider and one scope want it and that provider can take a forward-only
            // stream; otherwise it has to become a re-readable local file first. Preserving the
            // straight-through case is what keeps existing single-scope NFS deployments free of the
            // full-size temp-space requirement they never had.
            var straightThrough = providers.Count == 1
                && scopeIds.Count == 1
                && !providers[0].RequiresStagedFile;

            if (straightThrough)
            {
                var request = new IsoUploadRequest(viewId, scopeIds, destName, null,
                    () => isIso ? openUpload() : BuildIsoStream(openUpload(), filename));

                return await FanOutAsync(providers, p => p.UploadAsync(request, ct), "upload", "uploaded", destName);
            }

            string tempPath = null;

            try
            {
                tempPath = await StageIsoAsync(openUpload, filename, isIso, ct);
                var request = new IsoUploadRequest(viewId, scopeIds, destName, tempPath, null);

                return await FanOutAsync(providers, p => p.UploadAsync(request, ct), "upload", "uploaded", destName);
            }
            finally
            {
                DeleteIfExists(tempPath);
            }
        }

        public async Task<IsoUploadResult> DeleteAsync(Guid viewId, string scopeId, string filename, CancellationToken ct)
        {
            var providers = EnabledProviders;

            if (providers.Count == 0)
                throw new BadRequestException("No hypervisor is configured to store ISOs.");

            // Verbatim, deliberately: the name comes back out of a listing, which is the real stored name
            // on whichever provider holds the file. Folding it through every provider's normalizer here
            // would rewrite a name that predates a newly configured provider - a vSphere-only install that
            // later enables Proxmox could no longer delete its own "Win 10.iso", because the request would
            // become "Win_10.iso" and the NFS path treats a miss as success. Each provider normalizes for
            // its own storage internally instead.
            return await FanOutAsync(providers, p => p.DeleteAsync(viewId, scopeId, filename, ct), "delete", "deleted", filename);
        }

        // Fold a display filename through every enabled provider's character-set restrictions, so one
        // uploaded file has one name everywhere and the Files tab can merge rows by name. Order does not
        // matter and the normalizers are individually idempotent, so applying all of them is stable.
        // With only vSphere enabled every normalizer is the identity and the name is untouched.
        private static string NormalizeFilename(IReadOnlyList<IIsoProvider> providers, string filename)
        {
            foreach (var provider in providers)
            {
                filename = provider.NormalizeFilename(filename);
            }

            return filename;
        }

        // What one provider contributed to a fan-out. Threw distinguishes a provider that failed
        // outright - and so reached none of its targets - from one that reported some of its own hosts
        // failing, which only vSphere's multi-vCenter datastore mode can do.
        internal readonly record struct ProviderOutcome(VmType Provider, bool Threw, int FailedHostCount, int TotalHostCount);

        // One provider's listing, with the provider carried alongside it rather than stamped onto every
        // entry. Internal, and never serialized: which hypervisor answered is merge input, and a
        // provider's own identity is server-side only.
        internal readonly record struct ProviderListing(
            VmType Provider,
            IReadOnlyDictionary<Guid, IReadOnlyList<IsoListingEntry>> Isos);

        // Run one write operation on every provider concurrently and reduce the outcomes to the result
        // the API returns. A single provider's failure is caught and logged rather than faulting the
        // batch - the same tolerance VsphereService already applies across hosts - and only a total
        // failure throws.
        private async Task<IsoUploadResult> FanOutAsync(
            IReadOnlyList<IIsoProvider> providers,
            Func<IIsoProvider, Task<IsoOperationOutcome>> operation,
            string operationName,
            string pastTense,
            string filename)
        {
            var outcomes = await Task.WhenAll(providers.Select(async provider =>
            {
                try
                {
                    var outcome = await operation(provider);
                    return new ProviderOutcome(provider.ProviderType, false, outcome.FailedHostCount, outcome.TotalHostCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ISO {File} failed to {Operation} on provider {Provider}",
                        filename, operationName, provider.ProviderType);

                    // No host numbers invented for a provider that never ran: only it knows how many
                    // targets it would have had, and a storage-backed mode has none to report. The
                    // failure is carried by Threw, which is what sets PartialFailure.
                    return new ProviderOutcome(provider.ProviderType, true, 0, 0);
                }
            }));

            return SummarizeFanOut(outcomes, operationName, pastTense);
        }

        // The pure reduction of a fan-out to its result. Internal so the tests can exercise the message
        // wording and the counts without standing up an IsoService and a pair of fake hypervisors.
        internal static IsoUploadResult SummarizeFanOut(
            IReadOnlyList<ProviderOutcome> outcomes, string operationName, string pastTense)
        {
            // Anything short of complete success, whether a whole hypervisor or some of one's hosts.
            var failures = outcomes.Where(o => o.Threw || o.FailedHostCount > 0).ToList();

            if (outcomes.Count > 0 && outcomes.All(o => o.Threw))
            {
                throw new Exception(
                    $"ISO {operationName} failed on {DescribeFailures(failures)}. Try again, or contact an administrator if the issue persists.");
            }

            var failedHostCount = outcomes.Sum(o => o.FailedHostCount);
            var totalHostCount = outcomes.Sum(o => o.TotalHostCount);

            if (failures.Count > 0)
            {
                return new IsoUploadResult
                {
                    Message = $"ISO {pastTense}, but failed on {DescribeFailures(failures)}. Try again, or contact an administrator if the issue persists.",
                    FailedHostCount = failedHostCount,
                    TotalHostCount = totalHostCount,
                    PartialFailure = true
                };
            }

            return new IsoUploadResult
            {
                Message = $"ISO was {pastTense}",
                TotalHostCount = totalHostCount
            };
        }

        // Name the hypervisors an operation failed on. The host tally is only included for a provider
        // that had more than one target - "Proxmox (1 of 1 hosts)" is noise, since a Proxmox cluster is
        // always a single write target, whereas "Vsphere (1 of 3 hosts)" says the upload partly landed.
        private static string DescribeFailures(IReadOnlyList<ProviderOutcome> failures)
        {
            return JoinNames(failures
                .Select(f => f.TotalHostCount > 1
                    ? $"{f.Provider} ({f.FailedHostCount} of {f.TotalHostCount} hosts)"
                    : f.Provider.ToString()));
        }

        // "A", "A and B", "A, B and C".
        private static string JoinNames(IEnumerable<string> names)
        {
            var clauses = names.ToList();

            return clauses.Count switch
            {
                0 => "an unknown hypervisor",   // unreachable: only called with at least one name
                1 => clauses[0],
                _ => string.Join(", ", clauses.Take(clauses.Count - 1)) + " and " + clauses[^1]
            };
        }

        // Assemble the view-wide + per-team ISO listing for one or more Views. View-wide ISOs are keyed
        // on the view id; each team's on the team id.
        //
        // This is the management listing (the Files tab), which never mounts, so results from every
        // enabled provider are merged into one row per filename with MissingProviders recording where
        // the file is absent.
        public async Task<ManagedIsoResult[]> BuildViewIsoResultsAsync(IReadOnlyCollection<ViewTeams> views, CancellationToken ct)
        {
            // Scope the search to the single View when only one is requested (smaller search); otherwise
            // enumerate every View in one pass.
            var scopeViewId = views.Count == 1 ? views.First().View.Id : (Guid?)null;

            var providers = EnabledProviders;
            var results = await Task.WhenAll(providers.Select(async provider =>
            {
                try
                {
                    return (Type: provider.ProviderType, Isos: await provider.ListAsync(scopeViewId, ct), Error: (Exception)null);
                }
                catch (Exception ex)
                {
                    // A provider that cannot be listed right now is excluded from the merge entirely,
                    // rather than counted as "missing this file" - otherwise a transient outage would
                    // mark every row on every other provider as incomplete.
                    _logger.LogError(ex, "Failed to list ISOs from provider {Provider}", provider.ProviderType);
                    return (provider.ProviderType, null, ex);
                }
            }));

            var available = results.Where(r => r.Error == null).ToList();

            // Every provider failing is an error, not an empty listing: "no files" is rendered next to a
            // Delete button and read as "the upload never landed", so it must not be what an outage looks
            // like. A partial failure still degrades quietly, above.
            if (providers.Count > 0 && available.Count == 0)
            {
                throw new Exception(
                    $"Could not list ISOs from {JoinNames(results.Select(r => r.Type.ToString()))}. Try again, or contact an administrator if the issue persists.",
                    results[0].Error);
            }

            var isosByScope = MergeListings(
                available.Select(r => new ProviderListing(r.Type, r.Isos)).ToList());

            return views.Select(v => AssembleManagedIsoResult(v, isosByScope)).ToArray();
        }

        // Same shape as BuildViewIsoResultsAsync, but for the mount picker on a single VM - so it lists
        // from the ONE provider that VM belongs to. No merging: the rows are handed straight back to a
        // mount command, and only that provider's tokens are valid for that VM. It also preserves
        // vSphere's host affinity, where the datastore path must come from the host the VM runs on.
        public async Task<MountableIsoResult[]> BuildVmIsoResultsAsync(Guid vmId, VmType vmType, IReadOnlyCollection<ViewTeams> views, CancellationToken ct)
        {
            var provider = EnabledProviders.FirstOrDefault(p => p.ProviderType == vmType);

            if (provider == null)
                return views.Select(v => AssembleMountableIsoResult(v, new Dictionary<Guid, IReadOnlyList<IsoListingEntry>>())).ToArray();

            var scopeViewId = views.Count == 1 ? views.First().View.Id : (Guid?)null;
            var isosByScope = await provider.ListForVmAsync(vmId, scopeViewId, ct);

            return views.Select(v => AssembleMountableIsoResult(v, isosByScope)).ToArray();
        }

        // Collapse several providers' listings into one row per (scope, filename), recording which
        // providers were missing each file. Filenames are compared case-insensitively: the same upload
        // can come back cased differently from a case-preserving datastore and a normalizing one, and
        // showing it twice would be worse than picking one spelling.
        // Internal rather than private so the tests can exercise the merge directly - it is the one
        // piece of listing logic with enough cases (overlap, disjoint files, casing, a provider that
        // failed) to be worth testing without standing up a whole IsoService.
        internal static IReadOnlyDictionary<Guid, IReadOnlyList<ManagedIsoFile>> MergeListings(
            IReadOnlyList<ProviderListing> listings)
        {
            var availableProviderTypes = listings.Select(l => l.Provider).Distinct().ToList();
            var merged = new Dictionary<Guid, IReadOnlyList<ManagedIsoFile>>();

            foreach (var scopeId in listings.SelectMany(l => l.Isos.Keys).Distinct())
            {
                // The key is the merged row's filename: an OrdinalIgnoreCase dictionary keeps the
                // spelling of whichever provider reported the file first, which is the one to show.
                var providersByFilename = new Dictionary<string, HashSet<VmType>>(StringComparer.OrdinalIgnoreCase);

                foreach (var listing in listings)
                {
                    if (!listing.Isos.TryGetValue(scopeId, out var isos))
                        continue;

                    foreach (var iso in isos)
                    {
                        if (!providersByFilename.TryGetValue(iso.Filename, out var providers))
                        {
                            providers = new HashSet<VmType>();
                            providersByFilename[iso.Filename] = providers;
                        }

                        providers.Add(listing.Provider);
                    }
                }

                merged[scopeId] = providersByFilename
                    .Select(entry => new ManagedIsoFile(entry.Key)
                    {
                        MissingProviders = availableProviderTypes.Where(t => !entry.Value.Contains(t)).ToList()
                    })
                    .ToList();
            }

            return merged;
        }

        // The Views a VM's teams place it in, paired with the teams whose ISOs may be mounted on it.
        //
        // The Views come from the VM, but within each one the teams come from the caller's rights, not from
        // the VM: a team's ISO is mountable by anyone permitted to use it on a VM they may edit. Mounting
        // does publish the ISO to everyone who can reach the VM's console, but that is a deliberate act by
        // someone already authorized to edit the VM - no different from copying a file into it - so it is
        // not the picker's job to withhold the option.
        //
        // Membership is not the test; CanUseTeamIsoAsync is, so a caller scoped into a team they are not a
        // member of sees its ISOs. That is exactly what ResolveMountValueAsync enforces, so the picker
        // offers what a mount will accept.
        //
        // View-scoped ISOs need no team check: their audience is the whole View, which contains the VM.
        public async Task<IReadOnlyList<ViewTeams>> ResolveViewTeamsForVmAsync(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            var vmTeamIds = teamIds.Where(x => x != Guid.Empty).Distinct().ToList();
            var viewIds = await _viewService.GetViewIdsForTeams(vmTeamIds, ct);

            // Which View each of the VM's teams sits in, so a multi-View VM does not offer one View's
            // teams under another. Resolved once rather than per View; both lookups are cached.
            var viewIdByTeamId = new Dictionary<Guid, Guid>();
            foreach (var teamId in vmTeamIds)
            {
                var teamViewId = await _viewService.GetViewIdForTeam(teamId, ct);

                if (teamViewId.HasValue)
                    viewIdByTeamId[teamId] = teamViewId.Value;
            }

            var viewTeamsTasks = viewIds.Select(async viewId =>
            {
                // Null when Player does not know the View. Empty is not "no access" any more: a view-admin
                // who is not a member of any team still gets the View's ISOs and its teams' ISOs.
                var callerTeams = (await _playerService.GetTeamsByViewIdAsync(viewId, ct))?.ToList() ?? [];

                // Every team in the View is a candidate, so one of the caller's own teams - or one they are
                // only scoped into - is offered even where the VM is not on it. The privileged all-teams
                // listing is the complete set, but vm.api believing the caller may call it is no guarantee
                // player.api agrees: the VM's teams and the caller's are unioned in so a refusal degrades
                // to those rather than dropping rows CanUseTeamIsoAsync would have admitted.
                var allViewTeams = await GetAllViewTeamsOrEmptyAsync(viewId, ct);

                var candidates = allViewTeams
                    .Select(t => t.Id)
                    .Concat(vmTeamIds.Where(id => viewIdByTeamId.TryGetValue(id, out var v) && v == viewId))
                    .Concat(callerTeams.Select(t => t.Id))
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                var teams = new List<Team>();

                foreach (var teamId in candidates)
                {
                    if (!await CanUseTeamIsoAsync(teamId, ct))
                        continue;

                    // Named from whichever listing knows the team. Neither one having it means the caller
                    // is not a member and the privileged listing was refused, so the row degrades to an
                    // id-only one rather than failing the whole listing.
                    var team = callerTeams.FirstOrDefault(t => t.Id == teamId)
                        ?? allViewTeams.FirstOrDefault(t => t.Id == teamId)
                        ?? new Team { Id = teamId, Name = teamId.ToString() };

                    teams.Add(team);
                }

                // Nothing to show and no reason to think the caller can see this View at all.
                if (teams.Count == 0 && callerTeams.Count == 0)
                    return (ViewTeams)null;

                var view = await _playerService.GetViewByIdAsync(viewId, ct);

                // By name, because the candidate order is player.api's listing order - insertion order in
                // practice - which puts a team in a different place in the picker from one View to the next.
                return new ViewTeams(view, teams.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
            });

            return (await Task.WhenAll(viewTeamsTasks))
                .Where(vt => vt != null)
                .ToList();
        }

        // Authorize a mount value the client submitted, and return the token to actually mount.
        //
        // The value is never trusted and never passed through: the provider decodes the scope out of it
        // and rebuilds a canonical token (see IIsoProvider.ResolveMountTargetAsync), then the scope is
        // checked against the caller with the same permission set GetVmForEditing just applied to the VM.
        //
        // The scope's team need not be one of the VM's: mounting one team's ISO on another's VM exposes it
        // to that VM's console, but only because a caller authorized over both chose to publish it. What is
        // checked is that the scope is real and reachable from this VM - its View contains the VM, and its
        // team is in that View - and that the caller may use that scope's ISOs.
        //
        // Existence is deliberately not verified: the picker only offers files that exist, and a mount of
        // a correctly scoped ISO that has since been deleted is the caller's own team's problem, not
        // worth a datastore browse on every mount.
        public async Task<string> ResolveMountValueAsync(
            Guid vmId, VmType vmType, IEnumerable<Guid> vmTeamIds, string mountValue, CancellationToken ct)
        {
            var teamIds = vmTeamIds.Where(x => x != Guid.Empty).Distinct().ToList();
            var provider = EnabledProviders.FirstOrDefault(p => p.ProviderType == vmType);

            if (provider == null)
                throw RejectMount(vmId, mountValue, $"no enabled ISO provider for {vmType}");

            var target = await provider.ResolveMountTargetAsync(vmId, mountValue, ct);

            if (target == null)
                throw RejectMount(vmId, mountValue, $"not a Player-managed ISO on the {vmType} storage this Vm can reach");

            var vmViewIds = await _viewService.GetViewIdsForTeams(teamIds, ct);

            if (!vmViewIds.Contains(target.ViewId))
                throw RejectMount(vmId, mountValue, $"View {target.ViewId} does not contain this Vm");

            // ScopeId == ViewId is a view-scoped ISO: its audience is the whole View, and the caller has
            // already been authorized to edit a Vm in it, so there is nothing further to check.
            if (target.ScopeId != target.ViewId)
            {
                // Rejects a hand-crafted (view, team) pair from two different Views, which would encode a
                // scope no upload could ever have produced.
                if (await _viewService.GetViewIdForTeam(target.ScopeId, ct) != target.ViewId)
                    throw RejectMount(vmId, mountValue, $"Team {target.ScopeId} is not in View {target.ViewId}");

                if (!await CanUseTeamIsoAsync(target.ScopeId, ct))
                    throw RejectMount(vmId, mountValue, $"caller cannot edit team {target.ScopeId}");
            }

            return target.MountValue;
        }

        // Whether the caller may use a team's ISOs on a VM of that team. The same permission set
        // BaseHandler.GetVmForEditing applies to the VM's teams, so on a single-team VM this is implied by
        // the check the handler already made and it only bites where a VM is shared between teams - which
        // is the only place a team's ISO can reach an audience beyond that team.
        private Task<bool> CanUseTeamIsoAsync(Guid teamId, CancellationToken ct) =>
            _playerService.CanEditTeams([teamId], ct);

        private async Task<List<Team>> GetAllViewTeamsOrEmptyAsync(Guid viewId, CancellationToken ct)
        {
            try
            {
                return (await _playerService.GetAllTeamsByViewIdAsync(viewId, ct))?.ToList() ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not list all teams in View {ViewId} to name a team the caller is not a member of", viewId);
                return [];
            }
        }

        // One log line per refusal, because the response deliberately says nothing about why: the reason
        // would otherwise tell a caller probing for other tenants' ISOs exactly how far they got.
        private ForbiddenException RejectMount(Guid vmId, string mountValue, string reason)
        {
            _logger.LogInformation(
                "Refused to mount an ISO on Vm {VmId}: {Reason}. Submitted value: {MountValue}",
                vmId, reason, mountValue);

            return new ForbiddenException("The specified iso is not available to this Vm");
        }

        // Bucket the scope-keyed ISO listing into the view-wide + per-team shape. View-wide ISOs are
        // keyed on the view id; each team's on the team id. Scopes with no ISOs yield empty arrays.
        // One method per result shape: the two differ only in their element type, and a single generic
        // over three type parameters would cost more than the duplication saves.
        private static ManagedIsoResult AssembleManagedIsoResult(
            ViewTeams viewTeams, IReadOnlyDictionary<Guid, IReadOnlyList<ManagedIsoFile>> isosByScope)
        {
            ManagedIsoFile[] IsosFor(Guid scopeId) =>
                isosByScope.TryGetValue(scopeId, out var isos) ? isos.ToArray() : Array.Empty<ManagedIsoFile>();

            var result = new ManagedIsoResult
            {
                ViewId = viewTeams.View.Id,
                ViewName = viewTeams.View.Name,
                Isos = IsosFor(viewTeams.View.Id)
            };

            foreach (var team in viewTeams.Teams)
            {
                result.TeamIsoResults.Add(new ManagedTeamIsoResult
                {
                    TeamId = team.Id,
                    TeamName = team.Name,
                    Isos = IsosFor(team.Id)
                });
            }

            return result;
        }

        private static MountableIsoResult AssembleMountableIsoResult(
            ViewTeams viewTeams, IReadOnlyDictionary<Guid, IReadOnlyList<IsoListingEntry>> isosByScope)
        {
            MountableIsoFile[] IsosFor(Guid scopeId) =>
                isosByScope.TryGetValue(scopeId, out var isos)
                    ? isos.Select(x => new MountableIsoFile(x.Filename, x.MountValue)).ToArray()
                    : Array.Empty<MountableIsoFile>();

            var result = new MountableIsoResult
            {
                ViewId = viewTeams.View.Id,
                ViewName = viewTeams.View.Name,
                Isos = IsosFor(viewTeams.View.Id)
            };

            foreach (var team in viewTeams.Teams)
            {
                result.TeamIsoResults.Add(new MountableTeamIsoResult
                {
                    TeamId = team.Id,
                    TeamName = team.Name,
                    Isos = IsosFor(team.Id)
                });
            }

            return result;
        }

        // Resolve the staging directory and write the upload to a local temp file as a finished ISO.
        // Real ISOs are streamed to disk directly; any other file is wrapped into a single-file ISO.
        // The caller owns deleting the returned path - but only once this returns, so a failure
        // part-way through has to clean up the partial file here or it would be leaked.
        private async Task<string> StageIsoAsync(Func<Stream> openUpload, string filename, bool isIso, CancellationToken ct)
        {
            var stagingDir = string.IsNullOrWhiteSpace(_isoUploadOptions.TempStagingPath)
                ? Path.GetTempPath()
                : _isoUploadOptions.TempStagingPath;
            Directory.CreateDirectory(stagingDir);

            var tempPath = Path.Combine(stagingDir, Guid.NewGuid().ToString() + IsoFileNaming.Extension);

            try
            {
                using var sourceStream = openUpload();

                if (isIso)
                {
                    using var destStream = File.Create(tempPath);
                    await sourceStream.CopyToAsync(destStream, ct);
                }
                else
                {
                    BuildIso(sourceStream, filename, tempPath);
                }
            }
            catch
            {
                DeleteIfExists(tempPath);
                throw;
            }

            return tempPath;
        }

        // Best-effort removal of a staged temp file. Never throws: a cleanup failure must not mask the
        // outcome (or the exception) of the upload itself.
        private static void DeleteIfExists(string path)
        {
            if (path == null)
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* best-effort cleanup */ }
        }

        // Wrap an arbitrary uploaded file into a single-file ISO at destPath (Joliet, "PlayerIso" volume).
        private static void BuildIso(Stream source, string filename, string destPath)
        {
            var builder = NewIsoBuilder(source, filename);
            builder.Build(destPath);
        }

        // Same conversion, but produced as a stream rather than a file, so the straight-through upload
        // path can still accept a non-ISO file without forcing a full-size temp copy first. The
        // returned stream reads from `source` on demand, so disposing it is the caller's job.
        private static Stream BuildIsoStream(Stream source, string filename)
        {
            return NewIsoBuilder(source, filename).Build();
        }

        private static CDBuilder NewIsoBuilder(Stream source, string filename)
        {
            var builder = new CDBuilder
            {
                UseJoliet = true,
                VolumeIdentifier = "PlayerIso"
            };
            builder.AddFile(filename, source);
            return builder;
        }

        // GetPrimaryTeamByViewIdAsync returns null both when Player does not know the View and when the
        // caller simply has no primary team in it (e.g. a system operator who is not a member). Neither
        // is a server fault, so translate to 403 rather than letting a null deref surface as a 500.
        private async Task<Guid> GetPrimaryTeamIdOrThrowAsync(Guid viewId, CancellationToken ct)
        {
            var primaryTeam = await _playerService.GetPrimaryTeamByViewIdAsync(viewId, ct);

            if (primaryTeam == null)
                throw new ForbiddenException("You do not have an active team in this View");

            return primaryTeam.Id;
        }

        // Hoisted because Path.GetInvalidFileNameChars() allocates a fresh array on every call.
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        // Drop every character the filesystem will not accept in a name.
        public string SanitizeFilename(string filename) =>
            string.Concat(filename.Split(InvalidFileNameChars));

        // Parse the optional "teamIds" form field, which may arrive as repeated values and/or
        // comma-separated lists. Invalid/empty entries are ignored.
        public IReadOnlyList<Guid> ParseTeamIds(StringValues values)
        {
            var ids = new List<Guid>();
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Guid.TryParse(part, out var id))
                        ids.Add(id);
                }
            }
            return ids.Distinct().ToList();
        }
    }
}
