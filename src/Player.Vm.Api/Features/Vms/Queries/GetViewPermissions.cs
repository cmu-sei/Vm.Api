/*
Crucible
Copyright 2020 Carnegie Mellon University.
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