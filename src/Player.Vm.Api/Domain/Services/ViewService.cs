// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Infrastructure.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Player.Api.Client;

namespace Player.Vm.Api.Domain.Services
{
    public interface IViewService
    {
        Task<Guid?> GetViewIdForTeam(Guid teamId, CancellationToken ct);
        Task<Guid[]> GetViewIdsForTeams(IEnumerable<Guid> teamIds, CancellationToken ct);
    }

    public class ViewService : IViewService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ViewService> _logger;

        public ViewService(
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            ClientOptions clientOptions,
            ILogger<ViewService> logger)
        {
            _cache = cache;
            _logger = logger;

            var playerUri = new Uri(clientOptions.urls.playerApi);
            var httpClient = httpClientFactory.CreateClient("player-admin");
            httpClient.BaseAddress = playerUri;
            var playerApiClient = new PlayerApiClient(httpClient);
            _playerApiClient = playerApiClient;
        }


        public async Task<Guid?> GetViewIdForTeam(Guid teamId, CancellationToken ct)
        {
            Guid? viewId;

            if (!_cache.TryGetValue(teamId, out viewId))
            {
                try
                {
                    var team = await _playerApiClient.GetTeamAsync(teamId, ct);
                    viewId = team.ViewId;

                    _cache.Set(teamId, viewId, new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromHours(12)));
                }
                catch (Exception ex)
                {
                    viewId = null;
                    _logger.LogError(ex, $"Error Getting ViewId for TeamId: {teamId}");
                }
            }

            return viewId;
        }

        public async Task<Guid[]> GetViewIdsForTeams(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            var viewIds = new List<Guid>();

            foreach (var teamId in teamIds)
            {
                var viewId = await this.GetViewIdForTeam(teamId, ct);

                if (viewId.HasValue && !viewIds.Any(x => x == viewId.Value))
                {
                    viewIds.Add(viewId.Value);
                }
            }

            return viewIds.ToArray();
        }
    }
}
