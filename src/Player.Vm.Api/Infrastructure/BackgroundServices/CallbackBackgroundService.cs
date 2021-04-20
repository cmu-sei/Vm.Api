// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
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

namespace Player.Vm.Api.Infrastructure.BackgroundServices
{
    public interface ICallbackBackgroundService
    {
        void AddEvent(WebhookEvent e);
        Task<VmMap[]> CloneMaps(ViewCreated form, CancellationToken ct);
    }

    public class CallbackBackgroundService : ICallbackBackgroundService
    {
        private ActionBlock<WebhookEvent> _eventQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IPlayerService _playerService;
        private readonly IMapper _mapper;
        private readonly ILogger<CallbackBackgroundService> _logger;

        public CallbackBackgroundService(IServiceScopeFactory scopeFactory, IPlayerService playerService, IMapper mapper, ILogger<CallbackBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _playerService = playerService;
            _mapper = mapper;
            _logger = logger;
            
            _eventQueue = new ActionBlock<WebhookEvent>(async e => {
                switch (e.Name)
                {
                    case "View Created":
                        // Since payload field is of type object, the json serialization in player makes it into string containing json
                        var payload = JsonConvert.DeserializeObject<ViewCreated>(e.Payload.ToString());
                        await CloneMaps(payload, new CancellationToken());
                        break;
                    case "View Deleted":
                        throw new NotImplementedException();
                }
            });
        }

        public void AddEvent(WebhookEvent e)
        {
            _eventQueue.Post(e);
        }
        
        public async Task<VmMap[]> CloneMaps(ViewCreated form, CancellationToken ct)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<VmContext>();
                // var playerService = scope.ServiceProvider.GetRequiredService<PlayerService>();
                // var mapper = scope.ServiceProvider.GetRequiredService<Mapper>();
                
                // Get maps assigned to parent view
                var maps = await context.Maps
                    .Where(m => m.ViewId == form.ParentId)
                    .Include(m => m.Coordinates)
                    .ToListAsync();
                
                _logger.LogWarning("Maps queried");

                // if (maps.Count == 0)
                //     return null;

                // Views can't have duplicate teams, so use HashSet for constant time lookups
                var parentTeams = (await _playerService.GetTeamsByViewIdAsync(form.ParentId, ct)).ToHashSet();
                var childTeams = (await _playerService.GetTeamsByViewIdAsync(form.ViewId, ct)).ToHashSet();
                var clonedMaps = new List<VmMap>();

                _logger.LogWarning("Parent and child teams retrieved");

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

                _logger.LogWarning("For loop complete");
                
                var added = context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added);
                var updated = context.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified);
                
                await context.SaveChangesAsync(ct);

                _logger.LogWarning("Changes saved in db");

                return clonedMaps.ToArray();
            }
        }
    }
}