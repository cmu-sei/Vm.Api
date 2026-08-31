// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Options;
using TinCan;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Domain.Services;

public interface IXApiService
{
    Task TrackConsoleOpenedAsync(Guid vmId, IEnumerable<Guid> activeTeamIds, CancellationToken ct = default);
    Task TrackConsoleClosedAsync(Guid vmId, IEnumerable<Guid> activeTeamIds, CancellationToken ct = default);
    Task TrackPowerOperationAsync(Guid vmId, PowerOperation operation, CancellationToken ct = default);
    Task TrackIsoMountedAsync(Guid vmId, string iso, CancellationToken ct = default);
    Task TrackIsoUploadedAsync(Guid viewId, string scope, string filename, CancellationToken ct = default);
    Task TrackIsoDeletedAsync(Guid viewId, string scope, string filename, CancellationToken ct = default);
    Task TrackNetworkChangedAsync(Guid vmId, string adapter, string network, CancellationToken ct = default);
    Task TrackUserFollowedAsync(Guid userId, string userName, Guid viewId, Guid teamId, CancellationToken ct = default);
    Task TrackUserUnfollowedAsync(Guid userId, string userName, Guid viewId, CancellationToken ct = default);
}

public class XApiService : IXApiService
{
    private const string ProfileActivityId = "https://crucible.sei.cmu.edu/xapi/profile/v1";
    private const string ConsoleOpenedVerbId = "https://crucible.sei.cmu.edu/xapi/verbs/console-opened";
    private const string ConsoleClosedVerbId = "https://crucible.sei.cmu.edu/xapi/verbs/console-closed";

    private readonly VmContext _context;
    private readonly IViewService _viewService;
    private readonly ClaimsPrincipal _user;
    private readonly XApiOptions _options;
    private readonly IXApiQueueService _queue;
    private readonly ILogger<XApiService> _logger;

    public XApiService(
        VmContext context,
        IViewService viewService,
        IPrincipal user,
        XApiOptions options,
        IXApiQueueService queue,
        ILogger<XApiService> logger)
    {
        _context = context;
        _viewService = viewService;
        _user = user as ClaimsPrincipal;
        _options = options;
        _queue = queue;
        _logger = logger;
    }

    public static bool IsConfigured(XApiOptions options) =>
        options.Enabled &&
        !string.IsNullOrWhiteSpace(options.Endpoint) &&
        !string.IsNullOrWhiteSpace(options.Username) &&
        !string.IsNullOrWhiteSpace(options.Password) &&
        !string.IsNullOrWhiteSpace(options.ApiUrl) &&
        !string.IsNullOrWhiteSpace(options.PlayerApiUrl);

    public Task TrackConsoleOpenedAsync(Guid vmId, IEnumerable<Guid> activeTeamIds, CancellationToken ct = default)
    {
        return TrackConsoleLifecycleAsync(vmId, activeTeamIds, ConsoleOpenedVerbId, "opened", "console-opened", ct);
    }

    public Task TrackConsoleClosedAsync(Guid vmId, IEnumerable<Guid> activeTeamIds, CancellationToken ct = default)
    {
        return TrackConsoleLifecycleAsync(vmId, activeTeamIds, ConsoleClosedVerbId, "closed", "console-closed", ct);
    }

    private async Task TrackConsoleLifecycleAsync(
        Guid vmId,
        IEnumerable<Guid> activeTeamIds,
        string verbId,
        string verbDisplay,
        string verb,
        CancellationToken ct)
    {
        try
        {
            await EnqueueConsoleLifecycleAsync(vmId, activeTeamIds, verbId, verbDisplay, verb, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue xAPI {Verb} event for VM {VmId}.", verb, vmId);
        }
    }

    public Task TrackPowerOperationAsync(Guid vmId, PowerOperation operation, CancellationToken ct = default) =>
        TrackVmActionAsync(
            vmId,
            operation switch
            {
                PowerOperation.PowerOn => "power-on",
                PowerOperation.PowerOff => "power-off",
                PowerOperation.Shutdown => "shutdown",
                PowerOperation.Reboot => "reboot",
                PowerOperation.Revert => "snapshot-reverted",
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            },
            operation switch
            {
                PowerOperation.PowerOn => "powered on",
                PowerOperation.PowerOff => "powered off",
                PowerOperation.Shutdown => "shutdown requested",
                PowerOperation.Reboot => "reboot requested",
                PowerOperation.Revert => "snapshot reverted",
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            },
            operation switch
            {
                PowerOperation.PowerOn => "Power On",
                PowerOperation.PowerOff => "Power Off",
                PowerOperation.Shutdown => "Shutdown",
                PowerOperation.Reboot => "Reboot",
                PowerOperation.Revert => "Snapshot Revert",
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            },
            new Dictionary<string, string>
            {
                ["power-operation"] = operation.ToString()
            },
            ct);

    public Task TrackIsoMountedAsync(Guid vmId, string iso, CancellationToken ct = default) =>
        TrackVmActionAsync(
            vmId,
            "iso-mounted",
            "ISO mounted",
            "ISO Mount",
            new Dictionary<string, string>
            {
                ["iso"] = iso
            },
            ct);

    public Task TrackIsoUploadedAsync(Guid viewId, string scope, string filename, CancellationToken ct = default) =>
        TrackViewIsoActionAsync(viewId, "iso-uploaded", "ISO uploaded", scope, filename, ct);

    public Task TrackIsoDeletedAsync(Guid viewId, string scope, string filename, CancellationToken ct = default) =>
        TrackViewIsoActionAsync(viewId, "iso-deleted", "ISO deleted", scope, filename, ct);

    public Task TrackNetworkChangedAsync(Guid vmId, string adapter, string network, CancellationToken ct = default) =>
        TrackVmActionAsync(
            vmId,
            "network-changed",
            "network changed",
            "Network Change",
            new Dictionary<string, string>
            {
                ["network-adapter"] = adapter,
                ["network"] = network
            },
            ct);

    public Task TrackUserFollowedAsync(
        Guid userId,
        string userName,
        Guid viewId,
        Guid teamId,
        CancellationToken ct = default) =>
        TrackUserFollowAsync(userId, userName, viewId, teamId, "followed", "followed", ct);

    public Task TrackUserUnfollowedAsync(
        Guid userId,
        string userName,
        Guid viewId,
        CancellationToken ct = default) =>
        TrackUserFollowAsync(userId, userName, viewId, null, "unfollowed", "stopped following", ct);

    private async Task TrackUserFollowAsync(
        Guid userId,
        string userName,
        Guid viewId,
        Guid? teamId,
        string action,
        string verbDisplay,
        CancellationToken ct)
    {
        try
        {
            if (!IsConfigured(_options))
            {
                return;
            }

            var actor = CreateActor();
            if (actor is null)
            {
                _logger.LogWarning(
                    "Skipping xAPI user follow action {Action} because the authenticated user has no subject or issuer claim.",
                    action);
                return;
            }

            var extensions = new JObject
            {
                ["https://crucible.sei.cmu.edu/xapi/extensions/followed-user-id"] = userId.ToString()
            };
            if (teamId.HasValue)
            {
                extensions["https://crucible.sei.cmu.edu/xapi/extensions/followed-team-id"] = teamId.Value.ToString();
            }

            var name = new LanguageMap();
            name.Add("en-US", $"{userName ?? userId.ToString()} Console");

            var description = new LanguageMap();
            description.Add("en-US", "Virtual machine console followed by a user.");

            var activity = new Activity
            {
                id = $"{_options.ApiUrl.TrimEnd('/')}/views/{viewId}/users/{userId}/console",
                definition = new ActivityDefinition
                {
                    type = new Uri("http://activitystrea.ms/schema/1.0/application"),
                    name = name,
                    description = description,
                    extensions = new TinCan.Extensions(extensions)
                }
            };

            var statement = new Statement
            {
                actor = actor,
                verb = CreateVerb($"https://crucible.sei.cmu.edu/xapi/verbs/{action}", verbDisplay),
                target = activity,
                context = BuildContext(viewId)
            };

            await _queue.EnqueueAsync(new XApiQueuedStatementEntity
            {
                StatementJson = statement.ToJSON(true),
                Verb = action,
                ActivityId = activity.id,
                ViewId = viewId
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue xAPI user follow action {Action} for User {UserId}.", action, userId);
        }
    }

    private async Task EnqueueConsoleLifecycleAsync(
        Guid vmId,
        IEnumerable<Guid> activeTeamIds,
        string verbId,
        string verbDisplay,
        string verb,
        CancellationToken ct)
    {
        if (!IsConfigured(_options))
        {
            return;
        }

        var actor = CreateActor();
        if (actor is null)
        {
            _logger.LogWarning("Skipping xAPI console event because the authenticated user has no subject or issuer claim.");
            return;
        }

        var vm = await _context.Vms
            .Include(item => item.VmTeams)
            .SingleOrDefaultAsync(item => item.Id == vmId, ct);
        if (vm is null)
        {
            _logger.LogWarning("Skipping xAPI console event because VM {VmId} was not found.", vmId);
            return;
        }

        var activeTeamIdArray = activeTeamIds
            .Where(teamId => teamId != Guid.Empty)
            .Distinct()
            .ToArray();
        var teamIds = activeTeamIdArray.Length > 0
            ? activeTeamIdArray
            : vm.VmTeams.Select(team => team.TeamId).ToArray();
        var viewId = (await _viewService.GetViewIdsForTeams(teamIds, ct)).FirstOrDefault();
        var activity = BuildConsoleActivity(vm, activeTeamIdArray);

        var statement = new Statement
        {
            actor = actor,
            verb = CreateVerb(verbId, verbDisplay),
            target = activity,
            context = BuildContext(viewId)
        };

        await _queue.EnqueueAsync(new XApiQueuedStatementEntity
        {
            StatementJson = statement.ToJSON(true),
            Verb = verb,
            ActivityId = activity.id,
            ViewId = viewId == Guid.Empty ? null : viewId
        }, ct);
    }

    private async Task TrackVmActionAsync(
        Guid vmId,
        string action,
        string verbDisplay,
        string activityName,
        IReadOnlyDictionary<string, string> actionExtensions,
        CancellationToken ct)
    {
        try
        {
            if (!IsConfigured(_options))
            {
                return;
            }

            var actor = CreateActor();
            if (actor is null)
            {
                _logger.LogWarning(
                    "Skipping xAPI VM action {Action} because the authenticated user has no subject or issuer claim.",
                    action);
                return;
            }

            var vm = await _context.Vms
                .Include(item => item.VmTeams)
                .SingleOrDefaultAsync(item => item.Id == vmId, ct);
            if (vm is null)
            {
                _logger.LogWarning("Skipping xAPI VM action {Action} because VM {VmId} was not found.", action, vmId);
                return;
            }

            var teamIds = vm.VmTeams.Select(team => team.TeamId).ToArray();
            var viewId = (await _viewService.GetViewIdsForTeams(teamIds, ct)).FirstOrDefault();
            var activity = BuildVmActionActivity(vm, action, activityName, actionExtensions);

            var statement = new Statement
            {
                actor = actor,
                verb = CreateVerb($"https://crucible.sei.cmu.edu/xapi/verbs/{action}", verbDisplay),
                target = activity,
                context = BuildContext(viewId)
            };

            await _queue.EnqueueAsync(new XApiQueuedStatementEntity
            {
                StatementJson = statement.ToJSON(true),
                Verb = action,
                ActivityId = activity.id,
                ViewId = viewId == Guid.Empty ? null : viewId
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue xAPI VM action {Action} for VM {VmId}.", action, vmId);
        }
    }

    private async Task TrackViewIsoActionAsync(
        Guid viewId,
        string action,
        string verbDisplay,
        string scope,
        string filename,
        CancellationToken ct)
    {
        try
        {
            if (!IsConfigured(_options))
            {
                return;
            }

            var actor = CreateActor();
            if (actor is null)
            {
                _logger.LogWarning(
                    "Skipping xAPI ISO action {Action} because the authenticated user has no subject or issuer claim.",
                    action);
                return;
            }

            var name = new LanguageMap();
            name.Add("en-US", filename);

            var description = new LanguageMap();
            description.Add("en-US", $"ISO {action} by Player VM API.");

            var extensions = new JObject
            {
                ["https://crucible.sei.cmu.edu/xapi/extensions/iso-scope"] = scope
            };
            var activity = new Activity
            {
                id = $"{_options.ApiUrl.TrimEnd('/')}/views/{viewId}/isos/{Uri.EscapeDataString(filename)}",
                definition = new ActivityDefinition
                {
                    type = new Uri("http://id.tincanapi.com/activitytype/file"),
                    name = name,
                    description = description,
                    extensions = new TinCan.Extensions(extensions)
                }
            };

            var statement = new Statement
            {
                actor = actor,
                verb = CreateVerb($"https://crucible.sei.cmu.edu/xapi/verbs/{action}", verbDisplay),
                target = activity,
                context = BuildContext(viewId)
            };

            await _queue.EnqueueAsync(new XApiQueuedStatementEntity
            {
                StatementJson = statement.ToJSON(true),
                Verb = action,
                ActivityId = activity.id,
                ViewId = viewId
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue xAPI ISO action {Action} for View {ViewId}.", action, viewId);
        }
    }

    private Agent CreateActor()
    {
        var subject = _user?.FindFirst("sub")?.Value;
        var issuer = _options.IssuerUrl ?? _user?.FindFirst("iss")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
        {
            return null;
        }

        return new Agent
        {
            name = _user.FindFirst("name")?.Value ?? _user.Identity?.Name ?? "Unknown User",
            account = new AgentAccount
            {
                homePage = issuerUri,
                name = subject
            }
        };
    }

    private Activity BuildConsoleActivity(VmEntity vm, IReadOnlyCollection<Guid> activeTeamIds)
    {
        var extensions = BuildVmExtensions(vm);

        if (activeTeamIds.Count > 0)
        {
            extensions["https://crucible.sei.cmu.edu/xapi/extensions/active-team-ids"] =
                string.Join(",", activeTeamIds);
        }

        var name = new LanguageMap();
        name.Add("en-US", $"{vm.Name ?? vm.Id.ToString()} Console");

        var description = new LanguageMap();
        description.Add("en-US", "Virtual machine console.");

        return new Activity
        {
            id = $"{_options.ApiUrl.TrimEnd('/')}/vms/{vm.Id}/console",
            definition = new ActivityDefinition
            {
                type = new Uri("http://activitystrea.ms/schema/1.0/application"),
                name = name,
                description = description,
                extensions = new TinCan.Extensions(extensions)
            }
        };
    }

    private Activity BuildVmActionActivity(
        VmEntity vm,
        string action,
        string activityName,
        IReadOnlyDictionary<string, string> actionExtensions)
    {
        var extensions = BuildVmExtensions(vm);
        foreach (var extension in actionExtensions.Where(item => !string.IsNullOrWhiteSpace(item.Value)))
        {
            extensions[$"https://crucible.sei.cmu.edu/xapi/extensions/{extension.Key}"] = extension.Value;
        }

        var name = new LanguageMap();
        name.Add("en-US", $"{vm.Name ?? vm.Id.ToString()} {activityName}");

        var description = new LanguageMap();
        description.Add("en-US", $"Virtual machine {action} action was accepted by Player VM API.");

        return new Activity
        {
            id = $"{_options.ApiUrl.TrimEnd('/')}/vms/{vm.Id}/actions/{action}",
            definition = new ActivityDefinition
            {
                type = new Uri("http://adlnet.gov/expapi/activities/simulation"),
                name = name,
                description = description,
                extensions = new TinCan.Extensions(extensions)
            }
        };
    }

    private static JObject BuildVmExtensions(VmEntity vm)
    {
        var extensions = new JObject
        {
            ["https://crucible.sei.cmu.edu/xapi/extensions/vm-id"] = vm.Id.ToString(),
            ["https://crucible.sei.cmu.edu/xapi/extensions/vm-type"] = vm.Type.ToString(),
            ["https://crucible.sei.cmu.edu/xapi/extensions/team-ids"] =
                string.Join(",", vm.VmTeams.Select(team => team.TeamId))
        };

        if (!string.IsNullOrWhiteSpace(vm.Name))
        {
            extensions["https://crucible.sei.cmu.edu/xapi/extensions/vm-name"] = vm.Name;
        }

        if (vm.IpAddresses?.Length > 0)
        {
            extensions["https://crucible.sei.cmu.edu/xapi/extensions/vm-ip-addresses"] =
                string.Join(",", vm.IpAddresses);
        }

        return extensions;
    }

    private Context BuildContext(Guid viewId)
    {
        var context = new Context
        {
            platform = _options.Platform,
            language = "en-US",
            contextActivities = new ContextActivities
            {
                category =
                [
                    new Activity { id = ProfileActivityId }
                ]
            }
        };

        if (viewId != Guid.Empty)
        {
            context.registration = viewId;
            context.contextActivities.parent =
            [
                new Activity
                {
                    id = $"{_options.PlayerApiUrl.TrimEnd('/')}/views/{viewId}",
                    definition = new ActivityDefinition
                    {
                        type = new Uri("http://adlnet.gov/expapi/activities/simulation")
                    }
                }
            ];
        }

        return context;
    }

    private static Verb CreateVerb(string id, string display)
    {
        var languageMap = new LanguageMap();
        languageMap.Add("en-US", display);

        return new Verb
        {
            id = new Uri(id),
            display = languageMap
        };
    }
}
