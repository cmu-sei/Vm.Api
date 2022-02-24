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
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using AutoMapper.QueryableExtensions;
using System.Runtime.Serialization;
using Player.Vm.Api.Data;
using Player.Vm.Api.Infrastructure.Exceptions;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authorization;
using Player.Vm.Api.Domain.Services;

namespace Player.Vm.Api.Features.VmUsageLoggingSession
{
    public class Get
    {
        [DataContract(Name="GetVmUsageLoggingSessionQuery")]
        public class Query : IRequest<VmUsageLoggingSession>
        {
            /// <summary>
            /// The Id of the VmUsageLoggingSession to retrieve
            /// </summary>
            [DataMember]
            public Guid Id { get; set; }
        }

        public class Handler : IRequestHandler<Query, VmUsageLoggingSession>
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

            public async Task<VmUsageLoggingSession> Handle(Query request, CancellationToken cancellationToken)
            {
                if (!(await _playerService.IsSystemAdmin(cancellationToken)))
                    throw new ForbiddenException("You do not have permission to view Vm Usage Logs");

                var vmUsageLoggingSession =  await _db.VmUsageLoggingSessions
                    .ProjectTo<VmUsageLoggingSession>(_mapper.ConfigurationProvider)
                    .SingleOrDefaultAsync(e => e.Id == request.Id);


                if (vmUsageLoggingSession == null)
                    throw new EntityNotFoundException<VmUsageLoggingSession>();

                return vmUsageLoggingSession;
            }
        }
    }
}
