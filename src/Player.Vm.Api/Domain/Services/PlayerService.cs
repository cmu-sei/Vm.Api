// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Http;
using Player.Api.Client;
using Player.Vm.Api.Infrastructure.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Vm.Api.Domain.Services
{
    public interface IPlayerService
    {
        Task<bool> IsSystemAdmin(CancellationToken ct);
        Task<bool> CanManageTeamsAsync(IEnumerable<Guid> teamIds, bool all, CancellationToken ct);
        Task<bool> CanManageTeamAsync(Guid teamId, CancellationToken ct);
        Task<bool> CanAccessTeamsAsync(IEnumerable<Guid> teamIds, CancellationToken ct);
        Task<bool> CanAccessTeamAsync(Guid teamId, CancellationToken ct);
        Task<IEnumerable<Team>> GetTeamsByViewIdAsync(Guid viewId, CancellationToken ct);
        Task<Guid> GetPrimaryTeamByViewIdAsync(Guid viewId, CancellationToken ct);
        Task<Guid?> GetGroupIdForViewAsync(Guid viewId, CancellationToken ct);
        Task<View> GetViewByIdAsync(Guid viewId, CancellationToken ct);
        Task<Team> GetTeamById(Guid id);
        Task<IEnumerable<Permission>> GetPermissionsByViewIdAsync(Guid viewId, CancellationToken ct);
        Task<User> GetUserById(Guid id, CancellationToken ct);
        Task<IEnumerable<User>> GetUsersByTeamId(Guid teamId, CancellationToken ct);
    }

    public class PlayerService : IPlayerService
    {
        private readonly IPlayerApiClient _playerApiClient;
        private readonly Guid _userId;
        private Dictionary<Guid, Team> _teamCache;

        public PlayerService(IHttpContextAccessor httpContextAccessor, IPlayerApiClient playerApiClient)
        {
            _teamCache = new Dictionary<Guid, Team>();
            // This probably isn't the best way to prevent crashes when using client_credentials flow but it works for now
            try
            {
                _userId = httpContextAccessor.HttpContext.User.GetId();
            }
            catch (Exception e)
            {
                _userId = new Guid("9fd3c38e-58b0-4af1-80d1-1895af91f1f9");
            }
            _playerApiClient = playerApiClient;
        }

        public async Task<bool> IsSystemAdmin(CancellationToken ct)
        {
            var user = await _playerApiClient.GetUserAsync(_userId);

            return user.IsSystemAdmin;
        }

        public async Task<bool> CanManageTeamsAsync(IEnumerable<Guid> teamIds, bool all, CancellationToken ct)
        {
            var teamDict = new Dictionary<Guid, bool>();

            foreach (var teamId in teamIds)
            {
                teamDict.Add(teamId, false);

                try
                {
                    Team team;

                    if (!_teamCache.TryGetValue(teamId, out team))
                    {
                        team = await GetTeamById(teamId);

                        if (team == null)
                            continue;

                        _teamCache.Add(teamId, team);
                    }

                    if (team.CanManage)
                    {
                        teamDict[teamId] = true;

                        if (!all)
                            return true;
                    }
                }
                catch (Exception ex)
                {

                }
            }

            return !teamDict.Values.Any(v => v == false);
        }

        public async Task<bool> CanManageTeamAsync(Guid teamId, CancellationToken ct)
        {
            return await CanManageTeamsAsync(new List<Guid> { teamId }, true, ct);
        }

        public async Task<bool> CanAccessTeamsAsync(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            foreach (var teamId in teamIds)
            {
                try
                {
                    Team team;

                    if (!_teamCache.TryGetValue(teamId, out team))
                    {
                        team = await GetTeamById(teamId);

                        if (team == null)
                            continue;

                        _teamCache.Add(teamId, team);
                    }

                    if (team.CanManage || team.IsPrimary)
                        return true;
                }
                catch (Exception ex)
                {
                }
            }

            return false;
        }

        public async Task<bool> CanAccessTeamAsync(Guid teamId, CancellationToken ct)
        {
            return await CanAccessTeamsAsync(new List<Guid> { teamId }, ct);
        }

        public async Task<IEnumerable<Team>> GetTeamsByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            var teams = await _playerApiClient.GetUserViewTeamsAsync(viewId, _userId, ct);

            foreach (Team team in teams)
            {
                if (!_teamCache.ContainsKey(team.Id))
                {
                    _teamCache.Add(team.Id, team);
                }
            }

            return teams.Where(t => t.IsPrimary || t.CanManage);
        }

        public async Task<Guid> GetPrimaryTeamByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            var teams = await _playerApiClient.GetUserViewTeamsAsync(viewId, _userId, ct);

            foreach (Team team in teams)
            {
                if (!_teamCache.ContainsKey(team.Id))
                {
                    _teamCache.Add(team.Id, team);
                }
            }

            return teams
                .Where(t => t.IsPrimary)
                .Select(t => t.Id)
                .FirstOrDefault();
        }

        public async Task<Team> GetTeamById(Guid id)
        {
            try
            {
                return await _playerApiClient.GetTeamAsync(id);
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public async Task<Guid?> GetGroupIdForViewAsync(Guid viewId, CancellationToken ct)
        {
            var permissions = await _playerApiClient.GetUserViewPermissionsAsync(viewId, _userId, ct);

            if (permissions.Any(p => p.Key == "ViewAdmin" && p.Value == "true"))
            {
                return viewId;
            }

            var teamMembership = permissions.Where(p => p.Key == "TeamMember").FirstOrDefault();
            Guid teamId;

            if (teamMembership != null)
            {
                if (Guid.TryParse(teamMembership.Value, out teamId))
                {
                    return teamId;
                }
            }

            return null;
        }

        public async Task<View> GetViewByIdAsync(Guid viewId, CancellationToken ct)
        {
            return await _playerApiClient.GetViewAsync(viewId, ct);
        }

        public async Task<IEnumerable<Permission>> GetPermissionsByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            var permissions = await _playerApiClient.GetUserViewPermissionsAsync(viewId, _userId, ct);
            return permissions;
        }

        public async Task<User> GetUserById(Guid id, CancellationToken ct)
        {
            return await _playerApiClient.GetUserAsync(id, ct);
        }

        public async Task<IEnumerable<User>> GetUsersByTeamId(Guid teamId, CancellationToken ct)
        {
            return await _playerApiClient.GetTeamUsersAsync(teamId, ct);
        }
    }
}
