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
        private readonly VmContext _context;
        private readonly IPlayerService _playerService;
        private readonly IMapper _mapper;

        public CallbackBackgroundService(VmContext context, IPlayerService playerService, IMapper mapper)
        {
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
            _context = context;
            _playerService = playerService;
            _mapper = mapper;
        }

        public void AddEvent(WebhookEvent e)
        {
            _eventQueue.Post(e);
        }
        
        public async Task<VmMap[]> CloneMaps(ViewCreated form, CancellationToken ct)
        {
            // TODO: Why is there a disposed object exception?

            if (form.ParentId == Guid.Empty)
                return null;

            // Get maps assigned to parent view
            var maps = await _context.Maps
                .Where(m => m.ViewId == form.ParentId)
                .Include(m => m.Coordinates)
                .ToListAsync();

            // Views can't have duplicate teams, so use HashSet for constant time lookups
            var parentTeams = (await _playerService.GetTeamsByViewIdAsync(form.ParentId, ct)).ToHashSet();
            var childTeams = (await _playerService.GetTeamsByViewIdAsync(form.ViewId, ct)).ToHashSet();
            var clonedMaps = new List<VmMap>();

            // Create clones of maps assigned to the child view
            foreach (var map in maps)
            {
                var clone = map;
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

                _context.Maps.Add(clone);
                clonedMaps.Add(_mapper.Map<Domain.Models.VmMap, VmMap>(clone));
            }

            await _context.SaveChangesAsync();

            return clonedMaps.ToArray();
        }
    }
}