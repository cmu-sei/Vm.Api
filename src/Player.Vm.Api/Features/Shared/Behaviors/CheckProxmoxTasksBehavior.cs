// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Features.Shared.Interfaces;

namespace Player.Vm.Api.Features.Shared.Behaviors
{
    public class CheckProxmoxTasksBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    {
        private readonly IProxmoxTaskService _proxmoxTaskService;

        public CheckProxmoxTasksBehavior(IProxmoxTaskService proxmoxTaskService)
        {
            _proxmoxTaskService = proxmoxTaskService;
        }

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            var response = await next();

            if (typeof(ICheckProxmoxTasksRequest).IsAssignableFrom(typeof(TRequest)))
            {
                _proxmoxTaskService.CheckTasks();
            }

            return response;
        }
    }
}
