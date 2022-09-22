// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using Player.Vm.Api.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Player.Vm.Api.Domain.Services;
using System.Collections.Generic;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Vms
{
    public class GetVmPermissions
    {
        [DataContract(Name = "GetVmPermissions")]
        public class Query : IRequest<IEnumerable<Permissions>>
        {
            public Guid Id { get; set; }
        }

        public class Handler : IRequestHandler<Query, IEnumerable<Permissions>>
        {
            private readonly IVmService _vmService;
            private readonly VmContext _dbContext;
            private readonly IPermissionsService _permissionsService;

            public Handler(
                IVmService vmService,
                VmContext dbContext,
                IPermissionsService permissionsService)
            {
                _vmService = vmService;
                _dbContext = dbContext;
                _permissionsService = permissionsService;
            }

            public async Task<IEnumerable<Permissions>> Handle(Query request, CancellationToken cancellationToken)
            {
                var permissionList = new List<Permissions>();

                var vm = await _dbContext.Vms
                    .Include(x => x.VmTeams)
                    .SingleOrDefaultAsync(x => request.Id == x.Id);

                if (await _vmService.CanAccessVm(vm, cancellationToken))
                {
                    permissionList.AddRange(await _permissionsService.GetPermissions(vm.VmTeams.Select(x => x.TeamId), cancellationToken));
                }

                return permissionList;
            }
        }
    }
}