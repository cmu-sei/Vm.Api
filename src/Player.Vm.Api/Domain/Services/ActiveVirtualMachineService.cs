// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Player.Vm.Api.Domain.Services
{
    public interface IActiveVirtualMachineService
    {
        ActiveVirtualMachine GetActiveVirtualMachineForUser(Guid userId);
        Guid SetActiveVirtualMachineForUser(Guid userId, string username, Guid vmId, string connectionId, IEnumerable<Guid> teamIds);
        ActiveVirtualMachine UnsetActiveVirtualMachineForUser(Guid userId, string username, string connectionId);
        string[] GetActiveVirtualMachineUsers(Guid vmId);
    }

    public class ActiveVirtualMachineService : IActiveVirtualMachineService
    {
        private readonly ConcurrentDictionary<Guid, ActiveVirtualMachine> _activeVirtualMachines = new ConcurrentDictionary<Guid, ActiveVirtualMachine>();

        public ActiveVirtualMachineService()
        {
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