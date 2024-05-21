// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Player.Vm.Api.Domain.Services
{
    public interface IActiveVirtualMachineService
    {
        ActiveVirtualMachine GetActiveVirtualMachineForUser(Guid userId);
        Guid SetActiveVirtualMachineForUser(Guid userId, string username, Guid vmId, string connectionId, IEnumerable<Guid> teamIds);
        ActiveVirtualMachine UnsetActiveVirtualMachineForUser(Guid userId, string username, string connectionId);
        string[] GetActiveVirtualMachineUsers(Guid vmId);
        Task<Dictionary<Guid, IEnumerable<string>>> GetActiveVirtualMachineUsersByGroup(Guid vmId, ActiveVirtualMachine previousVm, CancellationToken ct);
    }

    public class ActiveVirtualMachineService : IActiveVirtualMachineService
    {
        private readonly ConcurrentDictionary<Guid, ActiveVirtualMachine> _activeVirtualMachines = new ConcurrentDictionary<Guid, ActiveVirtualMachine>();
        private readonly IServiceScopeFactory _scopeFactory;

        public ActiveVirtualMachineService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public ActiveVirtualMachine GetActiveVirtualMachineForUser(Guid userId)
        {
            if (_activeVirtualMachines.TryGetValue(userId, out ActiveVirtualMachine activeVm))
            {
                return activeVm;
            }
            else
            {
                return null;
            }
        }

        public Guid SetActiveVirtualMachineForUser(Guid userId, string username, Guid vmId, string connectionId, IEnumerable<Guid> teamIds)
        {
            var activeVm = new ActiveVirtualMachine(vmId, connectionId, teamIds, username);

            return _activeVirtualMachines.AddOrUpdate(userId, activeVm, (userId, v) =>
            {
                return activeVm;
            }).VmId;
        }

        public ActiveVirtualMachine UnsetActiveVirtualMachineForUser(Guid userId, string username, string connectionId)
        {
            // Only remove if connectionId matches previous
            // This avoids unsetting when a background tab gets closed/disconnected
            if (_activeVirtualMachines.TryGetValue(userId, out ActiveVirtualMachine currentVm))
            {
                var activeVm = new ActiveVirtualMachine(currentVm.VmId, connectionId, currentVm.TeamIds, username);
                var entry = new KeyValuePair<Guid, ActiveVirtualMachine>(userId, activeVm);
                var collection = (ICollection<KeyValuePair<Guid, ActiveVirtualMachine>>)_activeVirtualMachines;

                if (collection.Remove(entry))
                {
                    return activeVm;
                }
            }

            return null;
        }

        public string[] GetActiveVirtualMachineUsers(Guid vmId)
        {
            var users = _activeVirtualMachines.Where(kvp => kvp.Value.VmId == vmId).Select(kvp => kvp.Value.Username);
            if (users.ToArray().Length > 0)
            {
                return users.ToArray();
            }
            else
            {
                return null;
            }
        }

        public async Task<Dictionary<Guid, IEnumerable<string>>> GetActiveVirtualMachineUsersByGroup(Guid vmId, ActiveVirtualMachine previousVm, CancellationToken ct)
        {
            var dict = new Dictionary<Guid, List<string>>();
            var activeVms = _activeVirtualMachines.Where(kvp => kvp.Value.VmId == vmId).Select(x => x.Value).ToList();

            if (previousVm != null)
            {
                activeVms.Add(previousVm);
            }

            foreach (var activeVm in activeVms)
            {
                var groupIds = await GetGroupIds(activeVm, ct);

                foreach (var groupId in groupIds)
                {
                    List<string> userNames;
                    if (dict.ContainsKey(groupId))
                    {
                        userNames = dict[groupId];
                    }
                    else
                    {
                        userNames = new List<string>();
                        dict.Add(groupId, userNames);
                    }

                    if (activeVm != previousVm)
                    {
                        userNames.Add(activeVm.Username);
                    }
                }
            }

            return dict.ToDictionary(x => x.Key, x => x.Value.AsEnumerable());
        }

        private async Task<IEnumerable<Guid>> GetGroupIds(ActiveVirtualMachine vm, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();

            var groups = new List<Guid>();

            var viewIds = await scope.ServiceProvider.GetRequiredService<IViewService>().GetViewIdsForTeams(vm.TeamIds, ct);

            foreach (var viewId in viewIds)
            {
                groups.Add(viewId);
            }

            foreach (var teamId in vm.TeamIds)
            {
                groups.Add(teamId);
            }

            return groups.AsEnumerable();
        }
    }

    public class ActiveVirtualMachine : IEquatable<ActiveVirtualMachine>
    {
        public Guid VmId { get; set; }
        public string ConnectionId { get; set; }

        /// <summary>
        /// The Ids of the relevant primary teams of the user when they accessed this vm
        /// </summary>
        public IEnumerable<Guid> TeamIds { get; set; }
        public string Username { get; set; }

        public ActiveVirtualMachine(Guid vmId, string connectionId, IEnumerable<Guid> teamIds, string username)
        {
            VmId = vmId;
            ConnectionId = connectionId;
            TeamIds = teamIds;
            Username = username;
        }

        /// <summary>
        /// Returns true if VmId and ConnectionId are equal. Ignore other properties.
        /// </summary>
        public bool Equals(ActiveVirtualMachine other)
        {
            return other.VmId.Equals(VmId) && other.ConnectionId.Equals(ConnectionId);
        }
    }
}