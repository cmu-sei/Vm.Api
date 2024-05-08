// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Player.Vm.Api.Infrastructure.Exceptions;
using AutoMapper;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Vsphere.Options;
using System.Security.Claims;
using System.Security.Principal;
using Player.Vm.Api.Infrastructure.Extensions;
using Player.Vm.Api.Domain.Services;
using Player.Api.Client;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using System.Linq;

namespace Player.Vm.Api.Features.Vsphere
{
    public class Get
    {
        [DataContract(Name = "GetVsphereVirtualMachineQuery")]
        public class Query : IRequest<VsphereVirtualMachine>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Query, VsphereVirtualMachine>
        {
            private readonly IVmService _vmService;
            private readonly IMapper _mapper;
            private readonly IVsphereService _vsphereService;
            private readonly ILogger<Get> _logger;
            private readonly VsphereOptions _vsphereOptions;
            private readonly ClaimsPrincipal _user;

            public Handler(
                IVmService vmService,
                IMapper mapper,
                IVsphereService vsphereService,
                ILogger<Get> logger,
                VsphereOptions vsphereOptions,
                IPrincipal user,
                IPlayerService playerService,
                IPermissionsService permissionsService) :
                base(mapper, vsphereService, playerService, user, permissionsService, vmService)
            {
                _vmService = vmService;
                _mapper = mapper;
                _vsphereService = vsphereService;
                _logger = logger;
                _vsphereOptions = vsphereOptions;
                _user = user as ClaimsPrincipal;
            }

            public async Task<VsphereVirtualMachine> Handle(Query request, CancellationToken cancellationToken)
            {
                var vm = await _vmService.GetAsync(request.Id, cancellationToken);

                if (vm == null)
                    throw new EntityNotFoundException<VsphereVirtualMachine>();

                var vsphereVirtualMachine = await base.GetVsphereVirtualMachine(vm, cancellationToken);

                LogAccess(vm);

                return vsphereVirtualMachine;
            }

            private void LogAccess(Vms.Vm vm)
            {
                if (_vsphereOptions.LogConsoleAccess && _logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(new EventId(1),
                        $"User {_user.GetName()} ({_user.GetId()}) accessed console of {vm.Name} ({vm.Id})");
                }
            }
        }
    }
}