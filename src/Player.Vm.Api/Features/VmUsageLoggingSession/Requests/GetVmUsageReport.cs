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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vsphere;
using Player.Vm.Api.Infrastructure.Extensions;

namespace Player.Vm.Api.Features.VmUsageLoggingSession
{
    public class GetVmUsageReport
    {
        [DataContract(Name="GetVmUsageReportQuery")]
        public class Query : IRequest<List<VmUsageReport>>
        {
            /// <summary>
            /// The Id of the VmUsageLoggingSession to retrieve log entries for
            /// </summary>
            [DataMember]
            public DateTimeOffset ReportStart { get; set; }
            public DateTimeOffset ReportEnd { get; set; }
        }

        public class Handler : IRequestHandler<Query, List<VmUsageReport>>
        {
            private readonly VmLoggingContext _db;
            private readonly IMapper _mapper;
            private readonly IAuthorizationService _authorizationService;
            private readonly IPlayerService _playerService;
            private readonly Guid _userId;

            public Handler(
                VmLoggingContext db,
                IMapper mapper,
                IAuthorizationService authorizationService,
                IPrincipal principal,
                IPlayerService playerService)
            {
                _db = db;
                _mapper = mapper;
                _authorizationService = authorizationService;
                _userId = (principal as ClaimsPrincipal).GetId();
                _playerService = playerService;
            }

            public async Task<List<VmUsageReport>> Handle(Query request, CancellationToken cancellationToken)
            {
                var sessionList = await _db.VmUsageLoggingSessions
                    .Where(s =>
                        request.ReportStart.CompareTo(s.SessionStart) <= 0 &&
                        request.ReportEnd.CompareTo(s.SessionEnd) >= 0)
                    .ToListAsync(cancellationToken);
                var sessionIdList = sessionList.Select(s => s.Id);
                List<VmUsageReport> vmUsageReportList;
                var flatVmUsageLogEntryList = await _db.VmUsageLogEntries
                    .Where(e => sessionIdList.Contains(e.SessionId))
                    .Select(e => new {
                        SessionId = e.SessionId,
                        SessionName = e.Session.SessionName,
                        SessionStart = e.Session.SessionStart,
                        SessionEnd = e.Session.SessionEnd,
                        VmId = e.VmId,
                        VmName = e.VmName,
                        IpAddress = e.IpAddress,
                        UserId = e.UserId,
                        UserName = e.UserName,
                        VmActiveDT = e.VmActiveDT,
                        VmInactiveDT = e.VmInactiveDT
                    })
                    .ToListAsync();
                // non-system admins can only get a report of their own activity
                if (!await _playerService.IsSystemAdmin(cancellationToken))
                {
                    flatVmUsageLogEntryList = flatVmUsageLogEntryList
                        .Where(f => f.UserId == _userId)
                        .ToList();
                }
                vmUsageReportList = flatVmUsageLogEntryList
                    .GroupBy(e => new {
                                e.SessionId,
                                e.VmId,
                                e.UserId})
                    .Select(g => new VmUsageReport {
                        SessionId = g.Key.SessionId,
                        SessionName = g.FirstOrDefault().SessionName,
                        SessionStart = g.FirstOrDefault().SessionStart,
                        SessionEnd = g.FirstOrDefault().SessionEnd,
                        VmId = g.Key.VmId,
                        VmName = g.FirstOrDefault().VmName,
                        IpAddress = g.FirstOrDefault().IpAddress,
                        UserId = g.Key.UserId,
                        UserName = g.FirstOrDefault().UserName,
                        MinutesActive = (int)g.Sum(x => x.VmInactiveDT.Subtract(x.VmActiveDT).TotalMinutes) })
                    .OrderBy(r => r.UserName)
                    .ThenBy(r => r.SessionName)
                    .ThenBy(r => r.VmName)
                    .ToList();

                return vmUsageReportList;
            }
        }
    }
}
