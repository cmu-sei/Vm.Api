// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Player.Api.Client;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Infrastructure.BackgroundServices
{
    public interface ICallbackBackgroundService
    {
        Task AddEvent(WebhookEvent e);
    }

    public class CallbackBackgroundService : ICallbackBackgroundService
    {
        private ActionBlock<WebhookEvent> _eventQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMapper _mapper;
        private readonly ILogger<CallbackBackgroundService> _logger;
        private readonly IHttpClientFactory _clientFactory;

        public CallbackBackgroundService(IServiceScopeFactory scopeFactory, IMapper mapper, ILogger<CallbackBackgroundService> logger, IHttpClientFactory clientFactory)
        {
            _scopeFactory = scopeFactory;
            _mapper = mapper;
            _logger = logger;
            _clientFactory = clientFactory;

            _eventQueue = new ActionBlock<WebhookEvent>(async e => {
                switch (e.Name)
                {
                    case "View Created":
                        // Since payload field is of type object, the json serialization in player makes it into string containing json
                        var payloadCreate = JsonConvert.DeserializeObject<ViewCreated>(e.Payload.ToString());
                        await CloneMaps(payloadCreate, new CancellationToken());
                        break;
                    case "View Deleted":
                        var payloadDelete = JsonConvert.DeserializeObject<ViewDeleted>(e.Payload.ToString());
                        await DeleteClonedMaps(payloadDelete.ViewId, new CancellationToken());
                        break;
                }
            });
        }

        public async Task AddEvent(WebhookEvent e)
        {
            await _eventQueue.SendAsync(e);
        }
        
        private async Task<VmMap[]> CloneMaps(ViewCreated form, CancellationToken ct)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<VmContext>();
                var clientOptions = scope.ServiceProvider.GetRequiredService<ClientOptions>();

                var client = _clientFactory.CreateClient("player-admin");
                client.BaseAddress = new Uri(clientOptions.urls.playerApi);
                var playerClient = new PlayerApiClient(client);
                
                // Get maps assigned to parent view
                var maps = await context.Maps
                    .Where(m => m.ViewId == form.ParentId)
                    .Include(m => m.Coordinates)
                    .ToListAsync();

                if (maps.Count == 0)
                {
                    return null;
                }
                    
                // Views can't have duplicate teams, so use HashSet for constant time lookups
                var parentTeams = (await playerClient.GetViewTeamsAsync(form.ParentId)).ToHashSet();
                var childTeams = (await playerClient.GetViewTeamsAsync(form.ViewId)).ToHashSet();
                var clonedMaps = new List<VmMap>();

                // Create clones of maps assigned to the child view
                foreach (var map in maps)
                {
                    var clone = map.Clone();
                    // Set id manually so we can return the IDs of the new maps
                    clone.Id = Guid.NewGuid();
                    clone.ViewId = form.ViewId;

                    var teamNames = parentTeams
                        .Where(t => map.TeamIds.Contains((Guid) t.Id))
                        .Select(t => t.Name);
                    
                    var cloneTeamIds = childTeams
                        .Where(t => teamNames.Contains(t.Name))
                        .Select(t => (Guid) t.Id);

                    clone.TeamIds = cloneTeamIds.ToList();

                    context.Maps.Add(clone);
                    clonedMaps.Add(_mapper.Map<Domain.Models.VmMap, VmMap>(clone));
                }
    
                await context.SaveChangesAsync(ct);
                return clonedMaps.ToArray();
            }
        }

        // Delete all maps associated with the deleted view
        private async Task DeleteClonedMaps(Guid viewId, CancellationToken ct)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<VmContext>();
                
                var toDelete = await context.Maps
                    .Where(m => m.ViewId == viewId)
                    .Include(m => m.Coordinates)
                    .ToListAsync(ct);
                
                foreach (var map in toDelete)
                {
                    foreach (var coord in map.Coordinates)
                    {
                        context.Remove(coord);
                    }
                    context.Remove(map);
                }

                await context.SaveChangesAsync(ct);
            }
        }
    }
}