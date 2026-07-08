// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using AutoMapper;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Domain.Services;
using System.Security.Principal;
using Player.Vm.Api.Domain.Vsphere.Models;

namespace Player.Vm.Api.Features.Vsphere
{
    public class RunGuestProcess
    {
        [DataContract(Name = "RunGuestProcessOnVsphereVirtualMachine")]
        public class Command : IRequest<GuestProcessResult>
        {
            [JsonIgnore]
            public Guid Id { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string ProgramPath { get; set; }
            public string Arguments { get; set; }
            public string WorkingDirectory { get; set; }
            public int? TimeoutSeconds { get; set; }
        }

        public class Handler : BaseHandler, IRequestHandler<Command, GuestProcessResult>
        {
            private readonly IVsphereService _vsphereService;
            private readonly VsphereOptions _options;

            public Handler(
                IVsphereService vsphereService,
                VsphereOptions options,
                IVmService vmService,
                IMapper mapper,
                IPlayerService playerService,
                IPrincipal principal,
                IViewService viewService) :
                base(mapper, vsphereService, playerService, principal, vmService, viewService)
            {
                _vsphereService = vsphereService;
                _options = options;
            }

            public async Task<GuestProcessResult> Handle(Command request, CancellationToken cancellationToken)
            {
                var vm = await base.GetVmForEditing(request.Id, cancellationToken);
                var timeout = request.TimeoutSeconds.HasValue
                    ? TimeSpan.FromSeconds(request.TimeoutSeconds.Value)
                    : TimeSpan.FromSeconds(_options.GuestProcessDefaultTimeoutSeconds);

                return await _vsphereService.RunGuestProcess(
                    vm.Id,
                    request.Username,
                    request.Password,
                    request.ProgramPath,
                    request.Arguments,
                    request.WorkingDirectory,
                    timeout);
            }
        }
    }
}
