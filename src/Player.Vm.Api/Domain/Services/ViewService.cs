// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
        Task<TeamInfo[]> GetInfoForTeams(IEnumerable<Guid> teamIds, CancellationToken ct);
        Task<List<Guid>> GetTeamsForView(Guid viewId, CancellationToken ct);

        /// <summary>
        /// Every Team in the View, with names, fetched with this service's own Player credentials
        /// rather than the caller's. Use it where the caller is authorized by something other than
        /// membership - the caller's own "my teams in this View" list is narrowed by player.api before
        /// vm.api sees it, so it is not a usable source there.
        /// </summary>
        Task<Team[]> GetTeamDetailsForView(Guid viewId, CancellationToken ct);
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
            var teamInfo = await GetInfoForTeam(teamId, ct);

            return teamInfo.ViewId;
        }

        public async Task<Guid[]> GetViewIdsForTeams(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            var teamInfoList = await GetInfoForTeams(teamIds, ct);
            var viewIds = new List<Guid>();
            foreach (var teamInfo in teamInfoList)
            {
                if (teamInfo.ViewId != null && !viewIds.Any(x => x == teamInfo.ViewId))
                {
                    viewIds.Add((Guid)teamInfo.ViewId);
                }
            }

            return viewIds.ToArray();
        }

        public async Task<TeamInfo[]> GetInfoForTeams(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            var teamInfoList = new List<TeamInfo>();

            foreach (var teamId in teamIds)
            {
                var teamInfo = await this.GetInfoForTeam(teamId, ct);

                if (!teamInfoList.Any(x => x.ViewId == teamInfo.ViewId))
                {
                    teamInfoList.Add(teamInfo);
                }
            }

            return teamInfoList.ToArray();
        }

        public async Task<List<Guid>> GetTeamsForView(Guid viewId, CancellationToken ct)
        {
            return (await GetTeamDetailsForView(viewId, ct)).Select(x => x.Id).ToList();
        }

        public async Task<Team[]> GetTeamDetailsForView(Guid viewId, CancellationToken ct)
        {
            // Keyed by a prefixed string rather than the bare Guid: this cache is shared with the
            // per-team and per-user entries, which are keyed by bare Guids of their own.
            var key = $"view-teams-{viewId}";

            if (!_cache.TryGetValue(key, out Team[] teams))
            {
                teams = (await _playerApiClient.GetViewTeamsAsync(viewId, ct)).ToArray();
                _cache.Set(key, teams, new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(15)));
            }

            return teams;
        }

        private async Task<TeamInfo> GetInfoForTeam(Guid teamId, CancellationToken ct)
        {
            TeamInfo teamInfo;

            if (!_cache.TryGetValue(teamId, out teamInfo))
            {
                teamInfo = await GetTeamInfoFromPlayer(teamId, ct);
                _cache.Set(teamId, teamInfo, new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(15)));
            }

            return teamInfo;
        }

        private async Task<TeamInfo> GetTeamInfoFromPlayer(Guid teamId, CancellationToken ct)
        {
            var teamInfo = new TeamInfo();

            try
            {
                var team = await _playerApiClient.GetTeamAsync(teamId, ct);
                if (team != null)
                {
                    teamInfo.ViewId = team.ViewId;
                    teamInfo.TeamName = team.Name;
                    var view = await _playerApiClient.GetViewAsync(team.ViewId, ct);
                    teamInfo.ViewName = view.Name;
                }
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                _logger.LogWarning("Team {TeamId} not found in Player API", teamId);
            }

            return teamInfo;
        }
    }

    public class TeamInfo
    {
        public string TeamName { get; set; }
        public Guid? ViewId { get; set; }
        public string ViewName { get; set; }
    }
}
