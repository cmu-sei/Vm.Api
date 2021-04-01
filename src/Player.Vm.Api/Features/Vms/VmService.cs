// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Infrastructure.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Vm.Api.Features.Vms
{
    public interface IVmService
    {

        Task<Vm[]> GetAllAsync(CancellationToken ct);
        Task<Vm> GetAsync(Guid id, CancellationToken ct);
        Task<IEnumerable<Vm>> GetByTeamIdAsync(Guid teamId, string name, bool includePersonal, bool onlyMine, CancellationToken ct);
        Task<IEnumerable<Vm>> GetByViewIdAsync(Guid viewId, string name, bool includePersonal, bool onlyMine, CancellationToken ct);
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
        Task<SimpleTeam[]> GetTeamsAsync(Guid viewId, CancellationToken ct);
        Task<bool> CanAccessVm(Domain.Models.Vm vm, CancellationToken ct);
    }

    public class VmService : IVmService
    {
        private readonly VmContext _context;
        private readonly IPlayerService _playerService;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly IPermissionsService _permissionsService;

        public VmService(
            VmContext context,
            IPlayerService playerService,
            IPrincipal user,
            IMapper mapper,
            IPermissionsService permissionsService)
        {
            _context = context;
            _playerService = playerService;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _permissionsService = permissionsService;
        }

        public async Task<Vm[]> GetAllAsync(CancellationToken ct)
        {
            if (!(await _playerService.IsSystemAdmin(ct)))
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

            if (!await _playerService.CanAccessTeamsAsync(teamIds, ct))
                throw new ForbiddenException();

            if (vm.UserId.HasValue && vm.UserId != _user.GetId() && !await _playerService.CanManageTeamsAsync(teamIds, false, ct))
                throw new ForbiddenException("This machine belongs to another user");

            return true;
        }

        public async Task<IEnumerable<Vm>> GetByTeamIdAsync(Guid teamId, string name, bool includePersonal, bool onlyMine, CancellationToken ct)
        {
            if (!(await _playerService.CanAccessTeamAsync(teamId, ct)))
                throw new ForbiddenException();

            var vmQuery = _context.VmTeams
                .Where(v => v.TeamId == teamId)
                .Select(v => v.Vm)
                .Distinct();

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
                    if (!(await _playerService.CanManageTeamAsync(teamId, ct)))
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

            return _mapper.Map<IEnumerable<Vm>>(vmList);
        }

        public async Task<IEnumerable<Vm>> GetByViewIdAsync(Guid viewId, string name, bool includePersonal, bool onlyMine, CancellationToken ct)
        {
            List<Domain.Models.Vm> vmList = new List<Domain.Models.Vm>();
            var teams = await _playerService.GetTeamsByViewIdAsync(viewId, ct);
            var teamIds = teams.Select(t => t.Id);

            if (onlyMine)
            {
                var vmQuery = _context.VmTeams
                .Include(v => v.Vm)
                .Where(v => teamIds.Contains(v.TeamId))
                .Where(v => v.Vm.UserId.HasValue && v.Vm.UserId == _user.GetId());

                var vmTeams = await vmQuery.ToListAsync();
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

                    if (personalVms.Any())
                    {
                        if (!(await _playerService.CanManageTeamsAsync(teamIds, false, ct)))
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
            }

            return _mapper.Map<IEnumerable<Vm>>(vmList);
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

            if (!(await _playerService.CanManageTeamsAsync(formTeams, true, ct)))
                throw new ForbiddenException();

            if (!(await _permissionsService.CanWrite(formTeams, ct)))
                throw new ForbiddenException();

            var teamList = await _context.Teams
                .Where(t => formTeams.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct);

            foreach (var vmTeam in vmEntity.VmTeams)
            {
                if (!teamList.Contains(vmTeam.TeamId))
                {
                    _context.Teams.Add(new Domain.Models.Team() { Id = vmTeam.TeamId });
                }
            }

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

            if (!(await _playerService.CanManageTeamsAsync(teams, false, ct)))
                throw new ForbiddenException();

            if (!(await _permissionsService.CanWrite(teams, ct)))
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

            if (!(await _playerService.CanManageTeamsAsync(teamIds, false, ct)))
                throw new ForbiddenException();

            if (!(await _permissionsService.CanWrite(teamIds, ct)))
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

            if (!(await _playerService.CanManageTeamAsync(teamId, ct)))
                throw new ForbiddenException();

            if (!(await _permissionsService.CanWrite(new[] { teamId }, ct)))
                throw new ForbiddenException();

            var team = await _context.Teams.SingleOrDefaultAsync(t => t.Id == teamId, ct);

            if (team == null)
            {
                Domain.Models.Team te = new Domain.Models.Team { Id = teamId };
                _context.Teams.Add(te);
            }

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
            if (!(await _playerService.CanManageTeamAsync(teamId, ct)))
                throw new ForbiddenException();

            if (!(await _permissionsService.CanWrite(new[] { teamId }, ct)))
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

            if (form.TeamIds != null)
            {
                foreach (var m in existing)
                {
                    foreach (var id in form.TeamIds)
                        if (m.TeamIds.Contains(id))
                            throw new ForbiddenException("Cannot assign multiple maps to a single team");
                }
            }

            var mapIntermediate = _mapper.Map<VmMap>(form);
            mapIntermediate.ViewId = viewId;

            var mapEntity = _mapper.Map<Domain.Models.VmMap>(mapIntermediate);

            _context.Maps.Add(mapEntity);
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<VmMap>(mapEntity);
        }

        public async Task<VmMap[]> GetAllMapsAsync(CancellationToken ct)
        {
            if (!(await _playerService.IsSystemAdmin(ct)))
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

            var primTeam = await _playerService.GetPrimaryTeamByViewIdAsync(viewId, ct);
            // Only return the maps user has access to
            var accessableMaps = new List<Domain.Models.VmMap>();
            foreach (var m in maps)
            {
                if ((await _playerService.CanAccessTeamsAsync(m.TeamIds, ct) ||
                    (m.TeamIds.Count == 0 && await _playerService.CanManageTeamAsync(primTeam, ct))))
                    accessableMaps.Add(m);

            }

            return _mapper.Map<VmMap[]>(accessableMaps);
        }

        public async Task<VmMap> GetMapAsync(Guid mapId, CancellationToken ct)
        {
            var vmMap = await _context.Maps
                .Include(m => m.Coordinates)
                .Where(m => m.Id == mapId)
                .SingleOrDefaultAsync(ct);

            if (vmMap == null)
                return null;

            if (vmMap.TeamIds.Count > 0 && !(await _playerService.CanAccessTeamsAsync(vmMap.TeamIds, ct)))
                throw new ForbiddenException("You do not have access to this map");

            return _mapper.Map<VmMap>(vmMap);
        }

        public async Task<VmMap> GetTeamMapAsync(Guid teamId, CancellationToken ct)
        {
            // Check user can access this team
            if (!(await _playerService.CanAccessTeamAsync(teamId, ct)))
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

            if (vmMapEntity.TeamIds.Count > 0 && !(await _playerService.CanManageTeamsAsync(vmMapEntity.TeamIds, false, ct)))
                throw new ForbiddenException();

            _context.Maps.Remove(vmMapEntity);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<SimpleTeam[]> GetTeamsAsync(Guid viewId, CancellationToken ct)
        {
            var teams = await _playerService.GetTeamsByViewIdAsync(viewId, ct);

            var retTeams = new List<SimpleTeam>();
            foreach (var team in teams)
            {
                retTeams.Add(new SimpleTeam((Guid)team.Id, team.Name));
            }

            return retTeams.ToArray();
        }

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
                var viewTeamsModels = await _playerService.GetTeamsByViewIdAsync(viewId, ct);
                var viewTeams = viewTeamsModels.Select(t => t.Id).ToList();
                foreach (Guid teamId in teamIDs)
                {
                    if (await _playerService.GetTeamById(teamId) == null)
                        throw new ForbiddenException("Team with id " + teamId + " does not exist");

                    if (!(viewTeams.Contains(teamId)))
                        throw new ForbiddenException("Team with id " + teamId + " is not a member of the specified view");
                }

                // Check user can manage the teams
                if (!(await _playerService.CanManageTeamsAsync(teamIDs, true, ct)))
                    throw new ForbiddenException();
            }
        }

        #endregion
    }
}
