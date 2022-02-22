/*
Crucible
Copyright 2022 Carnegie Mellon University.
NO WARRANTY. THIS CARNEGIE MELLON UNIVERSITY AND SOFTWARE ENGINEERING INSTITUTE MATERIAL IS FURNISHED ON AN "AS-IS" BASIS. CARNEGIE MELLON UNIVERSITY MAKES NO WARRANTIES OF ANY KIND, EITHER EXPRESSED OR IMPLIED, AS TO ANY MATTER INCLUDING, BUT NOT LIMITED TO, WARRANTY OF FITNESS FOR PURPOSE OR MERCHANTABILITY, EXCLUSIVITY, OR RESULTS OBTAINED FROM USE OF THE MATERIAL. CARNEGIE MELLON UNIVERSITY DOES NOT MAKE ANY WARRANTY OF ANY KIND WITH RESPECT TO FREEDOM FROM PATENT, TRADEMARK, OR COPYRIGHT INFRINGEMENT.
Released under a MIT (SEI)-style license, please see license.txt or contact permission@sei.cmu.edu for full terms.
[DISTRIBUTION STATEMENT A] This material has been approved for public release and unlimited distribution.  Please see Copyright notice for non-US Government use and distribution.
Carnegie Mellon(R) and CERT(R) are registered in the U.S. Patent and Trademark Office by Carnegie Mellon University.
DM20-0181
*/

using System;
using System.Runtime.Serialization;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Player.Vm.Api.Data;
using Player.Vm.Api.Infrastructure.Exceptions;
using System.Collections.Generic;
using Player.Vm.Api.Domain.Services;

namespace Player.Vm.Api.Features.VmUsageLoggingSession
{
    public class CreateUsageLogVmActive
    {
        [DataContract(Name = "CreateUsageLogVmActiveCommand")]
        public class Command : IRequest<VmUsageLogEntry>
        {
            /// <summary>
            /// Data for a CreateUsageLogVmActive.
            /// </summary>
            [DataMember]        
            public Guid SessionId { get; set; }
            [DataMember]        
            public Guid MachineId { get; set; }
            [DataMember]        
            public string MachineName { get; set; }
            [DataMember]        
            public Guid UserId { get; set; }
            [DataMember]        
            public string UserName { get; set; }
            [DataMember]        
            public DateTimeOffset MachineOpen { get; set; }
            [DataMember]        
            public DateTimeOffset MachineClose { get; }

        }

        public class Handler : IRequestHandler<Command, VmUsageLogEntry>
        {
            private readonly VmLoggingContext _db;
            private readonly IMapper _mapper;
            private readonly IAuthorizationService _authorizationService;
            private readonly IPlayerService _playerService;

            public Handler(
                VmLoggingContext db,
                IMapper mapper,
                IAuthorizationService authorizationService,
                IPlayerService playerService)
            {
                _db = db;
                _mapper = mapper;
                _authorizationService = authorizationService;
                _playerService = playerService;
            }

            public async Task<VmUsageLogEntry> Handle(Command request, CancellationToken cancellationToken)
            {
                if (!(await _playerService.IsSystemAdmin(cancellationToken)))
                    throw new ForbiddenException("You do not have permission to create a Vm Usage Log");

                var entry = _db.VmUsageLoggingSessions.FirstOrDefault(e => e.Id == request.SessionId);

                if (entry == null)
                    throw new EntityNotFoundException<VmUsageLoggingSession>("Vm Usage Logging Session does not exist.");
                
                if (entry.SessionEnd > DateTimeOffset.MinValue)
                    throw new EntityNotFoundException<VmUsageLoggingSession>("Vm Usage Logging Session has already ended.");

                var logEntry = _mapper.Map<Domain.Models.VmUsageLogEntry>(request);
                logEntry.MachineOpen = DateTimeOffset.UtcNow;
                logEntry.MachineClose = DateTimeOffset.MinValue;

                await _db.VmUsageLogEntries.AddAsync(logEntry);
                await _db.SaveChangesAsync();
                return _mapper.Map<VmUsageLogEntry>(logEntry);
            }
        }
    }
}
