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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
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
    public class GetVmUsageCsvFile
    {
        [DataContract(Name="GetVmUsageCsvFileQuery")]
        public class Query : IRequest<FileResult>
        {
            /// <summary>
            /// The Id of the VmUsageLoggingSession to retrieve log entries for
            /// </summary>
            [DataMember]
            public Guid SessionId { get; set; }
        }

        public class Handler : IRequestHandler<Query, FileResult>
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

            public async Task<FileResult> Handle(Query request, CancellationToken cancellationToken)
            {
                var entry = _db.VmUsageLoggingSessions.FirstOrDefault(e => e.Id == request.SessionId);

                if (entry == null)
                    throw new EntityNotFoundException<VmUsageLoggingSession>();                

                if (!(await _playerService.IsSystemAdmin(cancellationToken) ||
                      await _playerService.IsViewAdmin(entry.ViewId, cancellationToken)))
                    throw new ForbiddenException("You do not have permission to view the specified Vm Usage Log");

                var vmUsageLogEntries =  await _db.VmUsageLogEntries
                    .ProjectTo<VmUsageLogEntry>(_mapper.ConfigurationProvider)
                    .Where(e => e.SessionId == request.SessionId)
                    .OrderByDescending(e => e.VmActiveDT)
                    .ToArrayAsync();

                if (vmUsageLogEntries == null)
                    throw new EntityNotFoundException<VmUsageLogEntry>();

                string fileName = entry.SessionName + ".csv";
                if (entry.SessionName.Length == 0)
                {
                    // No name giving, use Guid
                    fileName = entry.Id + ".csv";
                }

                string data = string.Join("\r\n", Array.ConvertAll(vmUsageLogEntries, s => {
                    return s.SessionId + ", " + 
                        s.Id + ", " + 
                        s.VmId  + ", " + 
                        s.VmName + ", " + 
                        s.IpAddress.Replace(", ", " ") + ", " +
                        s.UserId + ", " + 
                        s.UserName + ", " + 
                        s.VmActiveDT + ", " + 
                        s.VmInactiveDT;
                }));

                //Add header for CSV
                data = "SessionID, LogID, VmID, VmName, IpAddress, UserId, UserName, VmActiveDateTime, VmInActiveDateTime\r\n" + data;

                byte[] bytes = Encoding.ASCII.GetBytes(data);
                
                var result = new FileContentResult(bytes, "text/csv");
                result.FileDownloadName = fileName;

                return result;
            }
        }
    }
}
