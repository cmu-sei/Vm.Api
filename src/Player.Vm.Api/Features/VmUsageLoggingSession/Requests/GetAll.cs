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
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using AutoMapper.QueryableExtensions;
using Player.Vm.Api.Data;
using Player.Vm.Api.Infrastructure.Exceptions;
using Player.Vm.Api.Domain.Services;
using System.Linq;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Features.VmUsageLoggingSession
{
    public class GetAll
    {
        [DataContract(Name = "GetVmUsageLoggingSessionsQuery")]
        public class Query : IRequest<VmUsageLoggingSession[]>
        {
            /// <summary>
            /// Optional Bool when set to true only the active sessions will be returned
            /// </summary>
            [DataMember]
            public Nullable<Guid> ViewId { get; set; }
            [DataMember]
            public bool OnlyActive { get; set; }
        }

        public class Handler : IRequestHandler<Query, VmUsageLoggingSession[]>
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

            public async Task<VmUsageLoggingSession[]> Handle(Query request, CancellationToken cancellationToken)
            {
                if (!await _playerService.Can([], request.ViewId.HasValue ? [request.ViewId.Value] : [], [AppSystemPermission.ViewViews], [AppViewPermission.ViewView], [], cancellationToken))
                    throw new ForbiddenException("You do not have permission to view Vm Usage Logs.");

                if (request.OnlyActive == true)
                {
                    if (request.ViewId == null)
                    {
                        return await _db.VmUsageLoggingSessions
                            .ProjectTo<VmUsageLoggingSession>(_mapper.ConfigurationProvider)
                            .Where(s => s.SessionEnd <= DateTimeOffset.MinValue || s.SessionEnd > DateTimeOffset.UtcNow)
                            .OrderByDescending(s => s.CreatedDt)
                            .ToArrayAsync();
                    }
                    else
                    {
                        return await _db.VmUsageLoggingSessions
                            .ProjectTo<VmUsageLoggingSession>(_mapper.ConfigurationProvider)
                            .Where(s => s.ViewId == request.ViewId &&
                                        (s.SessionEnd <= DateTimeOffset.MinValue || s.SessionEnd > DateTimeOffset.UtcNow))
                            .OrderByDescending(s => s.CreatedDt)
                            .ToArrayAsync();
                    }

                }
                else
                {
                    if (request.ViewId == null)
                    {
                        return await _db.VmUsageLoggingSessions
                            .ProjectTo<VmUsageLoggingSession>(_mapper.ConfigurationProvider)
                            .OrderByDescending(s => s.CreatedDt)
                            .ToArrayAsync();
                    }
                    else
                    {
                        return await _db.VmUsageLoggingSessions
                            .ProjectTo<VmUsageLoggingSession>(_mapper.ConfigurationProvider)
                            .Where(s => s.ViewId == request.ViewId)
                            .OrderByDescending(s => s.CreatedDt)
                            .ToArrayAsync();
                    }

                }
            }
        }
    }
}
