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
using Player.Vm.Api.Data;
using AutoMapper;
using System.Runtime.Serialization;
using Player.Vm.Api.Infrastructure.Exceptions;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using AutoMapper.QueryableExtensions;

namespace Player.Vm.Api.Features.VmLoggingSessions
{
    public class Edit
    {
        [DataContract(Name="EditVmLoggingSessionCommand")]
        public class Command : IRequest<VmLoggingSession>
        {
            /// <summary>
            /// Data for a VmLoggingSession.
            /// </summary>
            [DataMember]
            public Guid Id { get; set; }
            [DataMember]
            public Guid TeamId { get; set; }
            [DataMember]
            public string TeamName { get; set; }
            [DataMember]
            public string SessionName { get; set; }
            [DataMember]
            public DateTimeOffset SessionStart { get; set; }
            [DataMember]
            public DateTimeOffset SessionEnd { get; set; }
        }

        public class Handler : IRequestHandler<Command, VmLoggingSession>
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

            public async Task<VmLoggingSession> Handle(Command request, CancellationToken cancellationToken)
            {
                //if (!(await _authorizationService.AuthorizeAsync(_user, null, new ContentDeveloperRequirement())).Succeeded)
                //    throw new ForbiddenException();

                var machine = await _db.VmLoggingSessions.FindAsync(request.Id);

                if (machine == null)
                    throw new EntityNotFoundException<VmLoggingSession>();

                _mapper.Map(request, machine);
                await _db.SaveChangesAsync();
                return _mapper.Map<VmLoggingSession>(machine);
            }
        }
    }
}
