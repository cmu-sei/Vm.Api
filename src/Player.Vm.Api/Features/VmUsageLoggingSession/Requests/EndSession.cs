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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Data;
using AutoMapper;
using System.Runtime.Serialization;
using Player.Vm.Api.Infrastructure.Exceptions;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using AutoMapper.QueryableExtensions;
using System.Text.Json.Serialization;
using Player.Vm.Api.Domain.Services;

namespace Player.Vm.Api.Features.VmUsageLoggingSession
{
    public class EndSession
    {
        [DataContract(Name="EndVmUsageLoggingSessionCommand")]
        public class Command : IRequest<VmUsageLoggingSession>
        {
            /// <summary>
            /// Data for a VmUsageLoggingSession.
            /// </summary>
            [JsonIgnore]
            public Guid Id { get; set; }
        }

        public class Handler : IRequestHandler<Command, VmUsageLoggingSession>
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

            public async Task<VmUsageLoggingSession> Handle(Command request, CancellationToken cancellationToken)
            {
                var entry = _db.VmUsageLoggingSessions.FirstOrDefault(e => e.Id == request.Id);

                if (entry == null)
                    throw new EntityNotFoundException<VmUsageLoggingSession>();                

                if (!(await _playerService.IsSystemAdmin(cancellationToken) ||
                      await _playerService.IsViewAdmin(entry.ViewId, cancellationToken)))
                    throw new ForbiddenException("You do not have permission to end the specified Vm Usage Log");

                entry.SessionEnd = DateTimeOffset.UtcNow;

                await _db.SaveChangesAsync();
                return _mapper.Map<VmUsageLoggingSession>(entry);
            }
        }
    }
}
