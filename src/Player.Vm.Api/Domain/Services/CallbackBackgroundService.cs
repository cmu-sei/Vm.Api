// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Player.Api.Client;
using Player.Vm.Api.Data;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Domain.Services;

public interface ICallbackBackgroundService
{
    Task AddEvent(WebhookEvent e);
}

public class CallbackBackgroundService : BackgroundService, ICallbackBackgroundService
{
    private ActionBlock<WebhookEventWrapper> _eventQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMapper _mapper;
    private readonly ILogger<CallbackBackgroundService> _logger;
    private readonly IHttpClientFactory _clientFactory;
    private readonly IOptionsMonitor<ClientOptions> _clientOptions;

    public CallbackBackgroundService(
        IServiceScopeFactory scopeFactory,
        IMapper mapper,
        ILogger<CallbackBackgroundService> logger,
        IHttpClientFactory clientFactory,
        IOptionsMonitor<ClientOptions> clientOptions)
    {
        _scopeFactory = scopeFactory;
        _mapper = mapper;
        _logger = logger;
        _clientFactory = clientFactory;
        _clientOptions = clientOptions;
        _eventQueue = new ActionBlock<WebhookEventWrapper>(async e => await ProcessEvent(e));
    }

    protected async override Task ExecuteAsync(CancellationToken ct)
    {
        // Add any pending events in the db to send queue
        using var scope = _scopeFactory.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<VmContext>();
        var events = await context.WebhookEvents
            .OrderBy(x => x.Timestamp)
            .ToListAsync(ct);

        foreach (var evt in events)
        {
            await AddEvent(evt);
        }
    }

    public async Task AddEvent(WebhookEvent e)
    {
        await _eventQueue.SendAsync(new WebhookEventWrapper(e));
    }

    private async Task AddEvent(WebhookEventWrapper e)
    {
        e.Attempts++;
        await _eventQueue.SendAsync(e);
    }

    private async Task ProcessEvent(WebhookEventWrapper e)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var vmContext = scope.ServiceProvider.GetRequiredService<VmContext>();
            var vmLoggingContext = scope.ServiceProvider.GetRequiredService<VmLoggingContext>();

            // Don't process if event is expired
            if (!e.IsExpired())
            {
                switch (e.WebhookEvent.Type)
                {
                    case EventType.ViewCreated:
                        // Since payload field is of type object, the json serialization in player makes it into string containing json
                        var payloadCreate = JsonConvert.DeserializeObject<ViewCreated>(e.WebhookEvent.Payload.ToString());
                        if (payloadCreate.ParentId.HasValue)
                        {
                            var client = _clientFactory.CreateClient("player-admin");
                            client.BaseAddress = new Uri(_clientOptions.CurrentValue.urls.playerApi);
                            var playerClient = new PlayerApiClient(client);
                            await CloneMaps(payloadCreate, vmContext, playerClient);
                            await CloneVmLoggingSessions(payloadCreate, vmLoggingContext, playerClient);
                        }
                        break;
                    case EventType.ViewDeleted:
                        var payloadDelete = JsonConvert.DeserializeObject<ViewDeleted>(e.WebhookEvent.Payload.ToString());
                        await DeleteClonedMaps(payloadDelete.ViewId, vmContext);
                        await EndVmLoggingSessions(payloadDelete.ViewId, vmLoggingContext);
                        break;
                }
            }

            // if no exceptions, remove this event from the db so we don't process it again
            vmContext.WebhookEvents.Remove(e.WebhookEvent);
            await vmContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception processing event with Id = {e.WebhookEvent?.Id}");

            // Add back to queue after a delay to retry
            _ = Task.Run(async () =>
            {
                await Task.Delay(new TimeSpan(0, 0, e.GetRetryDelaySeconds()));
                await AddEvent(e);
            });
        }
    }

    private async Task CloneMaps(ViewCreated form, VmContext dbContext, PlayerApiClient playerClient)
    {
        // Get maps assigned to parent view
        var maps = await dbContext.Maps
            .Where(m => m.ViewId == form.ParentId)
            .Include(m => m.Coordinates)
            .ToListAsync();

        if (maps.Count == 0)
        {
            return;
        }

        HashSet<Team> parentTeams = new HashSet<Team>();
        HashSet<Team> childTeams = new HashSet<Team>();

        // Views can't have duplicate teams, so use HashSet for constant time lookups
        try
        {
            parentTeams = (await playerClient.GetViewTeamsAsync(form.ParentId.Value)).ToHashSet();
            childTeams = (await playerClient.GetViewTeamsAsync(form.ViewId)).ToHashSet();
        }
        catch (ApiException ex)
        {
            // View no longer exists, so there is nothing to clone
            if (ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                return;
            }
        }

        var clonedMaps = new List<VmMap>();

        // Create clones of maps assigned to the child view
        foreach (var map in maps)
        {
            var clone = map.Clone();
            clone.ViewId = form.ViewId;

            var teamNames = parentTeams
                .Where(t => map.TeamIds.Contains((Guid)t.Id))
                .Select(t => t.Name);

            var cloneTeamIds = childTeams
                .Where(t => teamNames.Contains(t.Name))
                .Select(t => (Guid)t.Id);

            clone.TeamIds = cloneTeamIds.ToList();

            dbContext.Maps.Add(clone);
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task CloneVmLoggingSessions(ViewCreated form, VmLoggingContext dbContext, PlayerApiClient playerClient)
    {
        // Get sessions assigned to parent view
        var sessions = await dbContext.VmUsageLoggingSessions
            .Where(m => m.ViewId == form.ParentId)
            .ToListAsync();
        if (sessions.Any())
        {
            // Create clone for the child view with all of the child view teams
            var childTeams = (await playerClient.GetViewTeamsAsync(form.ViewId)).ToList();
            var clone = new Models.VmUsageLoggingSession();
            clone.ViewId = form.ViewId;
            clone.SessionName = form.ViewName;
            clone.CreatedDt = DateTimeOffset.UtcNow;
            clone.SessionStart = DateTimeOffset.UtcNow;
            clone.SessionEnd = DateTimeOffset.UtcNow.AddYears(1);

            var cloneTeamIds = childTeams.Select(t => (Guid)t.Id);
            clone.TeamIds = cloneTeamIds.ToArray();
            dbContext.VmUsageLoggingSessions.Add(clone);
            await dbContext.SaveChangesAsync();
        }
    }

    // Delete all maps associated with the deleted view
    private async Task DeleteClonedMaps(Guid viewId, VmContext dbContext)
    {
        var toDelete = await dbContext.Maps
            .Where(m => m.ViewId == viewId)
            .Include(m => m.Coordinates)
            .ToListAsync();

        foreach (var map in toDelete)
        {
            foreach (var coord in map.Coordinates)
            {
                dbContext.Remove(coord);
            }

            dbContext.Remove(map);
        }

        await dbContext.SaveChangesAsync();
    }

    // End all sessions associated with the deleted view
    private async Task EndVmLoggingSessions(Guid viewId, VmLoggingContext dbContext)
    {
        var sessionsToEnd = await dbContext.VmUsageLoggingSessions
            .Where(m => m.ViewId == viewId)
            .ToListAsync();

        foreach (var session in sessionsToEnd)
        {
            // if the current value in SessionEnd is later than the current time,
            // set SessionEnd to the current time
            var currentTime = DateTimeOffset.UtcNow;
            if (session.SessionEnd.CompareTo(currentTime) > 0)
            {
                session.SessionEnd = currentTime;
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private class WebhookEventWrapper
    {
        private const int ExpirationHours = 48;
        private const int InitialRetryDelaySeconds = 5;
        private const int IncrementRetryDelaySeconds = 5;
        private const int MaximumRetryDelaySeconds = 120;

        public WebhookEventWrapper(WebhookEvent e)
        {
            WebhookEvent = e;
        }

        public WebhookEvent WebhookEvent { get; }
        public bool FirstAttempt
        {
            get
            {
                return Attempts == 0;
            }
        }

        public int Attempts { get; set; }

        public bool IsExpired()
        {
            return !FirstAttempt && DateTime.UtcNow > WebhookEvent.Timestamp.AddHours(ExpirationHours);
        }

        public int GetRetryDelaySeconds()
        {
            var delay = InitialRetryDelaySeconds + (Attempts * IncrementRetryDelaySeconds);

            if (delay > MaximumRetryDelaySeconds)
            {
                delay = MaximumRetryDelaySeconds;
            }

            return delay;
        }
    }
}
