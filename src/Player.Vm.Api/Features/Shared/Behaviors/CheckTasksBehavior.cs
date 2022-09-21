// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Shared.Interfaces;

namespace Player.Vm.Api.Features.Shared.Behaviors
{
    public class CheckTasksBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    {
        private readonly ITaskService _taskService;

        public CheckTasksBehavior(ITaskService taskService)
        {
            _taskService = taskService;
        }

        public async Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate<TResponse> next)
        {
            var response = await next();

            if (typeof(ICheckTasksRequest).IsAssignableFrom(typeof(TRequest)))
            {
                _taskService.CheckTasks();
            }

            return response;
        }
    }
}
