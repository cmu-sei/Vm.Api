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
using Player.Vm.Api.Data;
using Player.Vm.Api.Infrastructure.Exceptions;
using System.Collections.Generic;

namespace Player.Vm.Api.Features.VmUsageLoggingSession
{
    public class Create
    {
        [DataContract(Name = "CreateVmUsageLoggingSessionCommand")]
        public class Command : IRequest<VmUsageLoggingSession>
        {
            /// <summary>
            /// Data for a VmUsageLoggingSession.
            /// </summary>
            [DataMember]
            public Guid TeamId { get; set; }
            [DataMember]
            public string TeamName { get; set; }
            [DataMember]
            public string SessionName { get; set; }
            [DataMember]
            public DateTimeOffset SessionStart { get; set; }


        }

        public class Handler : IRequestHandler<Command, VmUsageLoggingSession>
        {
            private readonly VmLoggingContext _db;
            private readonly IMapper _mapper;
            private readonly IAuthorizationService _authorizationService;
            private readonly ClaimsPrincipal _user;

            public Handler(
                VmLoggingContext db,
                IMapper mapper,
                IAuthorizationService authorizationService)
            {
                _db = db;
                _mapper = mapper;
                _authorizationService = authorizationService;
            }

            public async Task<VmUsageLoggingSession> Handle(Command request, CancellationToken cancellationToken)
            {
                //TODO: Chadif (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                //    throw new ForbiddenException();

                var loggingSession = _mapper.Map<Domain.Models.VmUsageLoggingSession>(request);
                loggingSession.SessionEnd = DateTimeOffset.MinValue;

                await _db.VmUsageLoggingSessions.AddAsync(loggingSession);
                await _db.SaveChangesAsync();
                return _mapper.Map<VmUsageLoggingSession>(loggingSession);
            }
        }
    }
}
