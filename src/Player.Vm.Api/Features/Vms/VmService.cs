// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Infrastructure.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Player.Vm.Api.Features.Networks;

namespace Player.Vm.Api.Features.Vms
{
    public interface IVmService
    {

        Task<Vm[]> GetAllAsync(CancellationToken ct);
        Task<Vm> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<Vm>> GetByTeamIdAsync(Guid teamId, string name, bool includePersonal, bool onlyMine, CancellationToken ct);
        Task<IEnumerable<Vm>> GetByViewIdAsync(Guid viewId, string name, bool includePersonal, bool onlyMine, CancellationToken ct);
        Task<IEnumerable<Vm>> GetAllByViewIdAsync(Guid viewId, CancellationToken ct);
        Task<Vm> CreateAsync(VmCreateForm form, CancellationToken ct);
        Task<Vm> UpdateAsync(Guid id, VmUpdateForm form, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<bool> AddToTeamAsync(Guid vmId, Guid teamId, CancellationToken ct);
        Task<bool> RemoveFromTeamAsync(Guid vmId, Guid teamId, CancellationToken ct);
        Task<VmMap> CreateMapAsync(VmMapCreateForm form, Guid viewId, CancellationToken ct);
        Task<VmMap[]> GetAllMapsAsync(CancellationToken ct);
        Task<VmMap> GetMapAsync(Guid mapId, CancellationToken ct);
        Task<VmMap> GetTeamMapAsync(Guid teamId, CancellationToken ct);
        Task<bool> DeleteMapAsync(Guid mapId, CancellationToken ct);
        Task<VmMap> UpdateMapAsync(VmMapUpdateForm form, Guid mapId, CancellationToken ct);
        Task<VmMap[]> GetViewMapsAsync(Guid viewId, CancellationToken ct);
        Task<VmMap[]> GetAllViewMapsAsync(Guid viewId, CancellationToken ct);
        Task<SimpleTeam[]> GetTeamsAsync(Guid viewId, CancellationToken ct);
        Task<bool> CanAccessVm(Domain.Models.Vm vm, CancellationToken ct);
        Task<EffectiveNetworkPermission> GetEffectiveNetworkPermissions(
            Guid viewId, IEnumerable<Guid> vmTeamIds,
            Domain.Models.VmType providerType, string providerInstanceId,
            CancellationToken ct);
    }

    public class VmService : IVmService
    {
        private readonly VmContext _context;
        private readonly IPlayerService _playerService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly INetworkService _networkService;

        public VmService(
            VmContext context,
            IPlayerService playerService,
            IPrincipal user,
            IMapper mapper,
            INetworkService networkService)
        {
            _context = context;
            _playerService = playerService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _networkService = networkService;
        }

        public async Task<Vm[]> GetAllAsync(CancellationToken ct)
        {
            // With no teams or Views to check, only a system-level Vm permission can pass.
            if (!await _playerService.CanViewVms([], [], ct))
                throw new ForbiddenException();

            var vms = await _context.Vms
                .Include(x => x.VmTeams)
                .ToArrayAsync(ct);

            return _mapper.Map<Vm[]>(vms);
        }

        public async Task<Vm> GetAsync(Guid id, CancellationToken ct)
        {
            var vmEntity = await _context.Vms
                .Include(v => v.VmTeams)
                .Where(v => v.Id == id)
                .SingleOrDefaultAsync(ct);

            await CanAccessVm(vmEntity, ct);
            var model = _mapper.Map<Vm>(vmEntity);
            return model;
        }

        public async Task<bool> CanAccessVm(Domain.Models.Vm vm, CancellationToken ct)
        {
            if (vm == null)
                throw new EntityNotFoundException<Vm>();

            var teamIds = vm.VmTeams.Select(x => x.TeamId);

            if (!await _playerService.CanViewVms(teamIds, null, ct))
                throw new ForbiddenException();

            // Someone else's personal Vm is only reachable with a Vm permission that reaches beyond a
            // single team - a team-scoped grant covers the team's shared Vms, not its members' own.
            if (vm.UserId.HasValue && vm.UserId != _user.GetId() &&
                !await _playerService.Can(
                    teamIds,
                    [],
                    AppPermissions.VmReadSystem,
                    AppPermissions.VmReadView,
                    [],
                    ct))
                throw new ForbiddenException("This machine belongs to another user");

            return true;
        }

        public async Task<IEnumerable<Vm>> GetByTeamIdAsync(Guid teamId, string name, bool includePersonal, bool onlyMine, CancellationToken ct)
        {
            var visibility = await _playerService.GetVisibilityContextForTeamAsync(teamId, ct);
            if (!visibility.TeamIds.Contains(teamId))
                throw new ForbiddenException();

            // Seeing a team is not the same as seeing its Vms; the caller needs a Vm permission too.
            if (!await _playerService.CanViewVms([teamId], null, ct))
                throw new ForbiddenException();

            IQueryable<Domain.Models.Vm> vmQuery = _context.VmTeams
                .Where(v => v.TeamId == teamId)
                .Select(v => v.Vm)
                .Distinct()
                .Include(v => v.VmTeams);

            if (onlyMine)
            {
                vmQuery = vmQuery.Where(v => v.UserId.HasValue && v.UserId == _user.GetId());
            }
            else if (!includePersonal)
            {
                vmQuery = vmQuery.Where(v => !v.UserId.HasValue);
            }

            if (!string.IsNullOrEmpty(name))
                vmQuery = vmQuery.Where(v => v.Name == name);

            // order the vms by name honoring trailing number as a number (i.e. abc1, abc2, abc10, abc11)
            var vmList = sortVmsByNumber(await vmQuery.ToListAsync(ct));

            if (includePersonal && !onlyMine)
            {
                var personalVms = vmList.Where(v => v.UserId.HasValue).ToList();

                if (personalVms.Any())
                {
                    if (!visibility.CanViewAllTeams)
                    {
                        foreach (var userVm in personalVms)
                        {
                            if (userVm.UserId.Value != _user.GetId())
                            {
                                vmList.Remove(userVm);
                            }
                        }
                    }
                }
            }

            return MapVisibleCollection(vmList, visibility.TeamIds);
        }

        public async Task<IEnumerable<Vm>> GetByViewIdAsync(Guid viewId, string name, bool includePersonal, bool onlyMine, CancellationToken ct)
        {
            List<Domain.Models.Vm> vmList = new List<Domain.Models.Vm>();
            var visibility = await _playerService.GetVisibilityContextAsync(viewId, ct);
            var teams = await _playerService.GetTeamsByViewIdAsync(viewId, ct);
            if (teams == null)
                return [];

            // Team visibility alone no longer implies Vm access, so everything below is scoped to the
            // teams whose Vms the caller may actually see rather than to every team in the View.
            // A View-level grant covers every team at once, so check that first rather than repeating
            // the same lookup per team. System permissions are deliberately not consulted: this is the
            // caller's own view of the View, and GetAllByViewIdAsync is the way in for an operator.
            var vmVisibleTeamIds = await _playerService.CanViewVmsAsMember([], [viewId], ct)
                ? visibility.TeamIds.ToHashSet()
                : await GetVmVisibleTeamIdsAsync(visibility.TeamIds, ct);

            var teamIds = teams.Select(t => t.Id).Where(vmVisibleTeamIds.Contains).ToArray();

            if (onlyMine)
            {
                var vmQuery = _context.VmTeams
                    .Include(v => v.Vm)
                    .ThenInclude(v => v.VmTeams)
                    .Where(v => teamIds.Contains(v.TeamId))
                    .Where(v => v.Vm.UserId.HasValue && v.Vm.UserId == _user.GetId());

                var vmTeams = await vmQuery.ToListAsync(ct);
                vmList = vmTeams.Select(v => v.Vm).Distinct().ToList();

                if (vmList.Count > 1)
                {
                    // Order by vm on user's primary team, since workstation app only looks at first result currently
                    var primaryTeam = teams.FirstOrDefault(t => t.IsPrimary);

                    if (primaryTeam != null)
                    {
                        vmList = vmList.OrderByDescending(v => v.VmTeams.Select(x => x.TeamId).Contains(primaryTeam.Id)).ToList();
                    }
                }
            }
            else
            {
                // We need to include team ids because sorting by team ids in front end requires having the team ids
                var vmQuery = _context.Vms
                    .Include(v => v.VmTeams)
                    .Where(v => v.VmTeams.Any(vt => teamIds.Contains(vt.TeamId)))
                    .Distinct();

                if (!includePersonal)
                {
                    vmQuery = vmQuery.Where(v => !v.UserId.HasValue);
                }

                if (!string.IsNullOrEmpty(name))
                    vmQuery = vmQuery.Where(v => v.Name == name);

                // order the vms by name honoring trailing number as a number (i.e. abc1, abc2, abc10, abc11)
                vmList = sortVmsByNumber(await vmQuery.ToListAsync(ct));

                if (includePersonal && !onlyMine)
                {
                    var personalVms = vmList.Where(v => v.UserId.HasValue).ToList();

                    foreach (var userVm in personalVms)
                    {
                        if (userVm.UserId.Value != _user.GetId() &&
                            !visibility.CanViewAllTeams)
                        {
                            vmList.Remove(userVm);
                        }
                    }
                }
            }

            // Team labels still follow team visibility - a shared Vm keeps naming a team the caller can
            // see even where it holds no Vm permission on that team.
            return MapVisibleCollection(vmList, visibility.TeamIds);
        }

        /// <summary>
        /// Every Vm in the View, personal ones included, whether or not the caller is on any of its
        /// teams. For callers whose access comes from a system- or View-level Vm permission rather than
        /// from membership - an administrator, or a service account such as Steamfitter's, which runs
        /// tasks against a View it was never added to. Null for a View player.api does not have.
        /// </summary>
        public async Task<IEnumerable<Vm>> GetAllByViewIdAsync(Guid viewId, CancellationToken ct)
        {
            if (!await _playerService.CanViewVms([], [viewId], ct))
                throw new ForbiddenException();

            var teamIds = (await _playerService.GetAllTeamIdsByViewIdAsync(viewId, ct))?.ToArray();

            if (teamIds == null)
                return null;

            var vms = await _context.Vms
                .Include(v => v.VmTeams)
                .Where(v => v.VmTeams.Any(vt => teamIds.Contains(vt.TeamId)))
                .ToListAsync(ct);

            // A Vm shared into another View keeps that View's teams off the result: the permission
            // checked above says nothing about them.
            return MapVisibleCollection(sortVmsByNumber(vms), teamIds.ToHashSet());
        }

        private IEnumerable<Vm> MapVisibleCollection(
            IEnumerable<Domain.Models.Vm> vmEntities,
            IReadOnlySet<Guid> visibleTeamIds)
        {
            var models = _mapper.Map<IEnumerable<Vm>>(vmEntities).ToArray();

            foreach (var model in models)
            {
                model.TeamIds = (model.TeamIds ?? [])
                    .Where(visibleTeamIds.Contains)
                    .ToArray();
            }

            return models;
        }

        public async Task<Vm> CreateAsync(VmCreateForm form, CancellationToken ct)
        {
            if (_context.Vms.Where(v => v.Id == form.Id).Any())
            {
                throw new ForbiddenException("Vm already exists");
            }

            var vmEntity = _mapper.Map<Domain.Models.Vm>(form);
            var formTeams = vmEntity.VmTeams.Select(v => v.TeamId).Distinct();

            if (!formTeams.Any())
                throw new ForbiddenException("Must include at least 1 team");

            List<Task<bool>> tasks = [];

            foreach (var teamId in formTeams)
            {
                tasks.Add(_playerService.CanManageTeams([teamId], ct));
            }

            await Task.WhenAll(tasks);

            if (tasks.Any(x => !x.Result))
                throw new ForbiddenException();

            _context.Vms.Add(vmEntity);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<Vm>(vmEntity);
        }

        public async Task<Vm> UpdateAsync(Guid id, VmUpdateForm form, CancellationToken ct)
        {
            var vmEntity = await _context.Vms.Where(v => v.Id == id).SingleOrDefaultAsync(ct);

            if (vmEntity == null)
                throw new EntityNotFoundException<Vm>();

            var teams = vmEntity.VmTeams.Select(v => v.TeamId).Distinct();

            if (!await _playerService.CanManageTeams(teams, ct))
                throw new ForbiddenException();

            vmEntity = _mapper.Map(form, vmEntity);

            _context.Vms.Update(vmEntity);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<Vm>(vmEntity);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var vmEntity = await _context.Vms
                .Include(v => v.VmTeams)
                .Where(v => v.Id == id)
                .SingleOrDefaultAsync(ct);

            if (vmEntity == null)
                throw new EntityNotFoundException<Vm>();

            var teamIds = vmEntity.VmTeams.Select(v => v.TeamId).Distinct();

            if (!await _playerService.CanManageTeams(teamIds, ct))
                throw new ForbiddenException();

            _context.Vms.Remove(vmEntity);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<bool> AddToTeamAsync(Guid vmId, Guid teamId, CancellationToken ct)
        {
            var vm = await _context.Vms.SingleOrDefaultAsync(v => v.Id == vmId, ct);

            if (vm == null)
                throw new EntityNotFoundException<Vm>();

            if (!await _playerService.CanManageTeams([teamId], ct))
                throw new ForbiddenException();

            var vmteam = await _context.VmTeams.SingleOrDefaultAsync(vt => vt.VmId == vmId && vt.TeamId == teamId);

            if (vmteam != null)
                return true;

            Domain.Models.VmTeam entity = new Domain.Models.VmTeam { VmId = vmId, TeamId = teamId };
            _context.VmTeams.Add(entity);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<bool> RemoveFromTeamAsync(Guid vmId, Guid teamId, CancellationToken ct)
        {
            if (!await _playerService.CanManageTeams([teamId], ct))
                throw new ForbiddenException();

            var vmteam = await _context.VmTeams.SingleOrDefaultAsync(vt => vt.VmId == vmId && vt.TeamId == teamId);
            var numTeams = await _context.VmTeams.Where(vt => vt.VmId == vmId).CountAsync();

            if (vmteam == null)
                return true;

            if (numTeams == 1)
                throw new ForbiddenException("Vm must be on at least one team");

            _context.VmTeams.Remove(vmteam);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<VmMap> CreateMapAsync(VmMapCreateForm form, Guid viewId, CancellationToken ct)
        {
            try
            {
                await validateViewAndTeams(form.TeamIds, viewId, ct);

            }
            catch (Exception ex)
            {
                throw new ForbiddenException(ex.Message);
            }

            // Check if the team already has a map.
            var existing = await _context.Maps
                .ToListAsync(ct);

            var mapIntermediate = _mapper.Map<VmMap>(form);
            mapIntermediate.ViewId = viewId;

            var mapEntity = _mapper.Map<Domain.Models.VmMap>(mapIntermediate);

            _context.Maps.Add(mapEntity);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<VmMap>(mapEntity);
        }

        public async Task<VmMap[]> GetAllMapsAsync(CancellationToken ct)
        {
            // With no teams or Views to check, only a system-level Map permission can pass.
            if (!await _playerService.CanViewMaps([], [], ct))
                throw new ForbiddenException();

            var maps = await _context.Maps
                .Include(x => x.Coordinates)
                .ToListAsync(ct);

            return _mapper.Map<VmMap[]>(maps);
        }

        public async Task<VmMap[]> GetViewMapsAsync(Guid viewId, CancellationToken ct)
        {
            var maps = await _context.Maps
                .Include(m => m.Coordinates)
                .Where(m => m.ViewId == viewId)
                .ToArrayAsync(ct);

            if (maps == null)
                return null;

            // This call is the View-existence probe, not a source of team data - the accessible-map
            // filter below asks the permission checks directly. A null result means the View is absent
            // from Player API, which must surface as 404 rather than an empty map list. Do not remove.
            var teams = await _playerService.GetTeamsByViewIdAsync(viewId, ct);

            if (teams == null)
                return null;

            // Every Map here is in the same View, so the View-level grant and the membership check each
            // only need asking once. Only team Maps the caller is not already covered for are left to
            // check one by one. As with GetByViewIdAsync, system permissions are not a way in here -
            // GetAllViewMapsAsync is.
            if (await _playerService.CanViewMapsAsMember([], [viewId], ct))
                return _mapper.Map<VmMap[]>(maps);

            var isInView = await _playerService.IsInViewAsync(viewId, ct);

            var accessible = await Task.WhenAll(maps.Select(async map =>
                (map, canView: map.TeamIds.Count == 0
                    ? isInView
                    : await _playerService.CanViewMapsAsMember(map.TeamIds, null, ct))));

            var accessibleMaps = accessible
                .Where(x => x.canView)
                .Select(x => x.map)
                .ToArray();

            return _mapper.Map<VmMap[]>(accessibleMaps);
        }

        /// <summary>
        /// Every Map in the View, for a caller with a system- or View-level Map permission - the Map
        /// counterpart of <see cref="GetAllByViewIdAsync"/>. Null for a View player.api does not have.
        /// </summary>
        public async Task<VmMap[]> GetAllViewMapsAsync(Guid viewId, CancellationToken ct)
        {
            if (!await _playerService.CanViewMaps([], [viewId], ct))
                throw new ForbiddenException();

            // Maps are stored against their View id, so the roster is only the existence probe - an
            // unknown View must be a 404, not an empty list.
            if (await _playerService.GetAllTeamIdsByViewIdAsync(viewId, ct) == null)
                return null;

            var maps = await _context.Maps
                .Include(m => m.Coordinates)
                .Where(m => m.ViewId == viewId)
                .ToArrayAsync(ct);

            return _mapper.Map<VmMap[]>(maps);
        }

        public async Task<VmMap> GetMapAsync(Guid mapId, CancellationToken ct)
        {
            var vmMap = await _context.Maps
                .Include(m => m.Coordinates)
                .Where(m => m.Id == mapId)
                .SingleOrDefaultAsync(ct);

            if (vmMap == null)
                return null;

            if (!await CanViewMapAsync(vmMap, ct))
                throw new ForbiddenException("You do not have access to this map");

            return _mapper.Map<VmMap>(vmMap);
        }

        /// <summary>
        /// A Map assigned to teams is readable with a Map permission on one of them, or at the View or
        /// system level. A Map assigned to no teams belongs to the View as a whole and is readable by
        /// everyone in that View - no Map permission at all. Managing one still takes ManageViewMaps or
        /// ManageMaps, so the open read does not make it editable.
        /// </summary>
        private async Task<bool> CanViewMapAsync(Domain.Models.VmMap map, CancellationToken ct)
        {
            if (await _playerService.CanViewMaps(map.TeamIds, [map.ViewId], ct))
                return true;

            return map.TeamIds.Count == 0 && await _playerService.IsInViewAsync(map.ViewId, ct);
        }

        public async Task<VmMap> GetTeamMapAsync(Guid teamId, CancellationToken ct)
        {
            if (!await _playerService.CanViewMaps([teamId], null, ct))
                throw new ForbiddenException();

            var maps = await _context.Maps
                .Include(m => m.Coordinates)
                .ToListAsync();

            foreach (Domain.Models.VmMap m in maps)
            {
                if (m.TeamIds.Contains(teamId))
                    return _mapper.Map<VmMap>(m);
            }
            return null;
        }

        public async Task<VmMap> UpdateMapAsync(VmMapUpdateForm form, Guid mapId, CancellationToken ct)
        {
            var vmMapEntity = await _context.Maps
                .Where(m => m.Id == mapId)
                .Include(m => m.Coordinates)
                .SingleOrDefaultAsync(ct);

            if (vmMapEntity == null)
                throw new EntityNotFoundException<VmMap>();

            try
            {
                await validateViewAndTeams(form.TeamIds, vmMapEntity.ViewId, ct);
            }
            catch (Exception ex)
            {
                throw new ForbiddenException(ex.Message);
            }

            vmMapEntity.Coordinates.Clear();
            foreach (var coord in form.Coordinates)
            {
                var tempCoord = _mapper.Map<Domain.Models.Coordinate>(coord);
                tempCoord.Id = Guid.Empty;

                vmMapEntity.Coordinates.Add(tempCoord);
            }

            vmMapEntity = _mapper.Map(form, vmMapEntity);

            await _context.SaveChangesAsync(ct);

            return _mapper.Map<VmMap>(vmMapEntity);
        }

        public async Task<bool> DeleteMapAsync(Guid mapId, CancellationToken ct)
        {
            var vmMapEntity = await _context.Maps
                .Include(m => m.Coordinates)
                .Where(m => m.Id == mapId)
                .SingleOrDefaultAsync(ct);

            if (vmMapEntity == null)
                throw new EntityNotFoundException<VmMap>();

            // A teamless Map has no team to check, so it falls through to the View-level Map permissions.
            if (!await _playerService.CanManageMaps(vmMapEntity.TeamIds, [vmMapEntity.ViewId], ct))
                throw new ForbiddenException();

            _context.Maps.Remove(vmMapEntity);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<SimpleTeam[]> GetTeamsAsync(Guid viewId, CancellationToken ct)
        {
            var teams = await _playerService.GetTeamsByViewIdAsync(viewId, ct);

            // View doesn't exist in Player API
            if (teams == null)
                return null;

            var retTeams = new List<SimpleTeam>();
            foreach (var team in teams)
            {
                retTeams.Add(new SimpleTeam((Guid)team.Id, team.Name));
            }

            return retTeams.ToArray();
        }

        public Task<EffectiveNetworkPermission> GetEffectiveNetworkPermissions(
            Guid viewId, IEnumerable<Guid> vmTeamIds,
            Domain.Models.VmType providerType, string providerInstanceId,
            CancellationToken ct)
            => _networkService.GetEffectiveNetworkPermissions(viewId, vmTeamIds, providerType, providerInstanceId, ct);

        #region Private

        // order the vms by name honoring trailing number as a number (i.e. abc1, abc2, abc10, abc11)
        private List<Domain.Models.Vm> sortVmsByNumber(List<Domain.Models.Vm> list)
        {
            var numchars = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };

            return list
                .OrderBy(v => v.Name.TrimEnd(numchars))
                .ThenBy(v => v.Name.TrimEnd(numchars).Length < v.Name.Length ?
                            int.Parse(v.Name.Substring(v.Name.TrimEnd(numchars).Length)) : 0)
                .ToList();
        }

        /// <summary>
        /// Narrows a set of visible teams to the ones whose Vms the caller may actually see. Team
        /// visibility is a Player-level concept; Vm access is a separate grant, so the two can differ.
        /// Run after a View-level check, which warms the per-View caches these parallel calls share.
        /// </summary>
        private async Task<HashSet<Guid>> GetVmVisibleTeamIdsAsync(IEnumerable<Guid> teamIds, CancellationToken ct)
        {
            var candidates = teamIds.Distinct().ToArray();

            var results = await Task.WhenAll(candidates.Select(async teamId =>
                (teamId, canView: await _playerService.CanViewVmsAsMember([teamId], null, ct))));

            return results
                .Where(x => x.canView)
                .Select(x => x.teamId)
                .ToHashSet();
        }

        private async Task validateViewAndTeams(List<Guid> teamIDs, Guid viewId, CancellationToken ct)
        {
            // Ensure view exists
            try
            {
                await _playerService.GetViewByIdAsync(viewId, ct);
            }
            catch (Exception)
            {
                throw new ForbiddenException("View does not exist");
            }

            // If this map is being assigned to team(s), ensure that the user is allowed to do so
            if (teamIDs != null && teamIDs.Count > 0)
            {
                // Make sure all teams exist and are part of this view
                foreach (Guid teamId in teamIDs)
                {
                    var team = await _playerService.GetTeamById(teamId);

                    if (team == null)
                        throw new ForbiddenException("Team with id " + teamId + " does not exist");

                    if (team.ViewId != viewId)
                        throw new ForbiddenException("Team with id " + teamId + " is not a member of the specified view");
                }

                // Check the user can manage each team's Maps. One CanManageMaps(teamIDs) would not do:
                // it passes if any one of the teams matches.
                var canManage = await Task.WhenAll(teamIDs.Select(t => _playerService.CanManageMaps([t], null, ct)));

                if (!canManage.All(x => x))
                    throw new ForbiddenException();
            }
            else if (!await _playerService.CanManageMaps([], [viewId], ct))
            {
                // A Map with no teams belongs to the View as a whole, so writing it takes a View-level
                // (or system-level) grant. Previously this branch checked nothing at all. Reading such
                // a Map is deliberately open to every View member - see CanViewMapAsync.
                throw new ForbiddenException();
            }
        }

        #endregion
    }
}
