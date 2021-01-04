// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
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
using Player.Api.Models;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Features.Vms
{
    public class GetViewPermissions
    {
        [DataContract(Name = "GetViewPermissions")]
        public class Query : IRequest<IEnumerable<Permissions>>
        {
            public Guid Id { get; set; }
        }

        public class Handler : IRequestHandler<Query, IEnumerable<Permissions>>
        {
            private readonly IPermissionsService _permissionsService;

            public Handler(
                IPermissionsService permissionsService)
            {
                _permissionsService = permissionsService;
            }

            public async Task<IEnumerable<Permissions>> Handle(Query request, CancellationToken cancellationToken)
            {
                return await _permissionsService.GetViewPermissions(request.Id, cancellationToken);
            }
        }
    }
}