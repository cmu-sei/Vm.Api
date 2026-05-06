// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Extensions;
using Player.Vm.Api.Infrastructure.Options;
using TinCan;

namespace Player.Vm.Api.Domain.Services;

public interface IXApiService
{
    bool IsConfigured();
    Task EmitVmConsoleAccessedAsync(Guid vmId, Guid viewId, CancellationToken ct = default);
    Task EmitVmConsoleClosedAsync(Guid vmId, Guid viewId, CancellationToken ct = default);
}

public class XApiService : IXApiService
{
    private readonly VmContext _context;
    private readonly ClaimsPrincipal _user;
    private readonly XApiOptions _xApiOptions;
    private readonly IXApiQueueService _queueService;
    private readonly ILogger<XApiService> _logger;
    private Agent _agent;

    public XApiService(
        VmContext context,
        IPrincipal user,
        XApiOptions xApiOptions,
        IXApiQueueService queueService,
        ILogger<XApiService> logger)
    {
        _context = context;
        _user = user as ClaimsPrincipal;
        _xApiOptions = xApiOptions;
        _queueService = queueService;
        _logger = logger;
    }

    private async Task EnsureAgentInitializedAsync(CancellationToken ct = default)
    {
        if (_agent != null || !IsConfigured())
            return;

        var account = new AgentAccount
        {
            name = _user.Identities.First().Claims.First(c => c.Type == "sub")?.Value
        };

        var iss = _user.Identities.First().Claims.First(c => c.Type == "iss")?.Value;
        if (!string.IsNullOrWhiteSpace(_xApiOptions.IssuerUrl))
        {
            account.homePage = new Uri(_xApiOptions.IssuerUrl);
        }
        else if (iss.Contains("http"))
        {
            account.homePage = new Uri(iss);
        }
        else
        {
            account.homePage = new Uri("http://" + iss);
        }

        var userId = _user.GetId();
        var userName = _user.Claims.FirstOrDefault(c => c.Type == "name")?.Value ?? "Unknown User";

        _agent = new Agent
        {
            name = userName,
            account = account
        };
    }

    public bool IsConfigured()
    {
        return _xApiOptions.Enabled && !string.IsNullOrWhiteSpace(_xApiOptions.Username);
    }

    private Context BuildContext(Guid viewId)
    {
        var context = new Context
        {
            registration = viewId,
            platform = _xApiOptions.Platform,
            language = "en-US"
        };

        var contextActivities = new ContextActivities
        {
            category = new List<Activity>
            {
                new Activity
                {
                    id = "https://crucible.sei.cmu.edu/xapi/profile/v1"
                }
            }
        };

        context.contextActivities = contextActivities;
        return context;
    }

    public async Task EmitVmConsoleAccessedAsync(Guid vmId, Guid viewId, CancellationToken ct = default)
    {
        _logger.LogInformation("EmitVmConsoleAccessedAsync called for VM {VmId}, View {ViewId}, IsConfigured: {IsConfigured}",
            vmId, viewId, IsConfigured());

        if (!IsConfigured())
        {
            _logger.LogWarning("xAPI is not configured - skipping statement emission");
            return;
        }

        try
        {
            await EnsureAgentInitializedAsync(ct);

            var vm = await _context.Vms
                .Include(v => v.VmTeams)
                .FirstOrDefaultAsync(v => v.Id == vmId, ct);
            if (vm == null)
            {
                _logger.LogWarning("Cannot emit VmConsoleAccessed: VM {VmId} not found", vmId);
                return;
            }

            var verb = new Verb { id = new Uri("http://id.tincanapi.com/verb/accessed") };
            verb.display = new LanguageMap();
            verb.display.Add("en-US", "accessed");

            var activity = new Activity { id = $"{_xApiOptions.ApiUrl}/vms/{vmId}/console" };
            activity.definition = new ActivityDefinition
            {
                type = new Uri("http://activitystrea.ms/schema/1.0/application")
            };
            activity.definition.name = new LanguageMap();
            activity.definition.name.Add("en-US", $"{vm.Name} Console");
            activity.definition.description = new LanguageMap();
            activity.definition.description.Add("en-US", $"Virtual machine console for {vm.Name}");

            // Add VM details as extensions
            var extensionsDict = new Dictionary<string, string>
            {
                ["https://crucible.sei.cmu.edu/xapi/extensions/vm-id"] = vmId.ToString(),
                ["https://crucible.sei.cmu.edu/xapi/extensions/vm-name"] = vm.Name,
                ["https://crucible.sei.cmu.edu/xapi/extensions/vm-type"] = vm.Type.ToString()
            };

            if (vm.IpAddresses != null && vm.IpAddresses.Length > 0)
            {
                extensionsDict["https://crucible.sei.cmu.edu/xapi/extensions/vm-ip-addresses"] = string.Join(", ", vm.IpAddresses);
            }

            if (vm.VmTeams != null && vm.VmTeams.Count > 0)
            {
                var teamIds = string.Join(", ", vm.VmTeams.Select(vt => vt.TeamId));
                extensionsDict["https://crucible.sei.cmu.edu/xapi/extensions/team-ids"] = teamIds;
            }

            activity.definition.extensions = new TinCan.Extensions(Newtonsoft.Json.Linq.JObject.FromObject(extensionsDict));

            var contextObj = BuildContext(viewId);

            // Add parent context activity (the View)
            // Views are owned by Player API, so use PlayerApiUrl
            var parentActivity = new Activity { id = $"{_xApiOptions.PlayerApiUrl}/views/{viewId}" };
            parentActivity.definition = new ActivityDefinition
            {
                type = new Uri("http://adlnet.gov/expapi/activities/simulation")
            };
            contextObj.contextActivities.parent = new List<Activity> { parentActivity };

            var statement = new Statement
            {
                actor = _agent,
                verb = verb,
                target = activity,
                context = contextObj
            };

            await _queueService.EnqueueAsync(new XApiQueuedStatementEntity
            {
                StatementJson = statement.ToJSON(true),
                Verb = "accessed",
                ActivityId = activity.id,
                ViewId = viewId
            }, ct);

            _logger.LogInformation("Queued VmConsoleAccessed statement for VM {VmId}, View {ViewId}", vmId, viewId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to emit VmConsoleAccessed for VM {VmId}, View {ViewId}", vmId, viewId);
        }
    }

    public async Task EmitVmConsoleClosedAsync(Guid vmId, Guid viewId, CancellationToken ct = default)
    {
        _logger.LogInformation("EmitVmConsoleClosedAsync called for VM {VmId}, View {ViewId}, IsConfigured: {IsConfigured}",
            vmId, viewId, IsConfigured());

        if (!IsConfigured())
        {
            _logger.LogWarning("xAPI is not configured - skipping statement emission");
            return;
        }

        try
        {
            await EnsureAgentInitializedAsync(ct);

            var vm = await _context.Vms
                .Include(v => v.VmTeams)
                .FirstOrDefaultAsync(v => v.Id == vmId, ct);
            if (vm == null)
            {
                _logger.LogWarning("Cannot emit VmConsoleClosed: VM {VmId} not found", vmId);
                return;
            }

            var verb = new Verb { id = new Uri("http://adlnet.gov/expapi/verbs/exited") };
            verb.display = new LanguageMap();
            verb.display.Add("en-US", "exited");

            var activity = new Activity { id = $"{_xApiOptions.ApiUrl}/vms/{vmId}/console" };
            activity.definition = new ActivityDefinition
            {
                type = new Uri("http://activitystrea.ms/schema/1.0/application")
            };
            activity.definition.name = new LanguageMap();
            activity.definition.name.Add("en-US", $"{vm.Name} Console");
            activity.definition.description = new LanguageMap();
            activity.definition.description.Add("en-US", $"Virtual machine console for {vm.Name}");

            // Add VM details as extensions
            var extensionsDict = new Dictionary<string, string>
            {
                ["https://crucible.sei.cmu.edu/xapi/extensions/vm-id"] = vmId.ToString(),
                ["https://crucible.sei.cmu.edu/xapi/extensions/vm-name"] = vm.Name,
                ["https://crucible.sei.cmu.edu/xapi/extensions/vm-type"] = vm.Type.ToString()
            };

            if (vm.IpAddresses != null && vm.IpAddresses.Length > 0)
            {
                extensionsDict["https://crucible.sei.cmu.edu/xapi/extensions/vm-ip-addresses"] = string.Join(", ", vm.IpAddresses);
            }

            if (vm.VmTeams != null && vm.VmTeams.Count > 0)
            {
                var teamIds = string.Join(", ", vm.VmTeams.Select(vt => vt.TeamId));
                extensionsDict["https://crucible.sei.cmu.edu/xapi/extensions/team-ids"] = teamIds;
            }

            activity.definition.extensions = new TinCan.Extensions(Newtonsoft.Json.Linq.JObject.FromObject(extensionsDict));

            var contextObj = BuildContext(viewId);

            // Add parent context activity (the View)
            // Views are owned by Player API, so use PlayerApiUrl
            var parentActivity = new Activity { id = $"{_xApiOptions.PlayerApiUrl}/views/{viewId}" };
            parentActivity.definition = new ActivityDefinition
            {
                type = new Uri("http://adlnet.gov/expapi/activities/simulation")
            };
            contextObj.contextActivities.parent = new List<Activity> { parentActivity };

            var statement = new Statement
            {
                actor = _agent,
                verb = verb,
                target = activity,
                context = contextObj
            };

            await _queueService.EnqueueAsync(new XApiQueuedStatementEntity
            {
                StatementJson = statement.ToJSON(true),
                Verb = "exited",
                ActivityId = activity.id,
                ViewId = viewId
            }, ct);

            _logger.LogInformation("Queued VmConsoleClosed statement for VM {VmId}, View {ViewId}", vmId, viewId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to emit VmConsoleClosed for VM {VmId}, View {ViewId}", vmId, viewId);
        }
    }
}
