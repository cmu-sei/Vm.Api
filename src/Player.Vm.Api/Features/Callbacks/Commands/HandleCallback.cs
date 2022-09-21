// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Domain.Services;
using Player.Api.Client;
using Player.Vm.Api.Data;

namespace Player.Vm.Api.Features.Callbacks
{
    public class HandleCallback
    {
        public class Command : IRequest<bool>
        {
            public WebhookEvent CallbackEvent { get; set; }
        }

        public class Handler : IRequestHandler<Command, bool>
        {
            private readonly VmContext _context;
            private readonly ICallbackBackgroundService _callbackBackgroundService;

            public Handler(
                VmContext context,
                ICallbackBackgroundService callbackBackgroundService)
            {
                _context = context;
                _callbackBackgroundService = callbackBackgroundService;
            }


            public async Task<bool> Handle(Command request, CancellationToken cancellationToken)
            {
                await _context.WebhookEvents.AddAsync(request.CallbackEvent);
                if (await _context.SaveChangesAsync() > 0)
                {
                    await _callbackBackgroundService.AddEvent(request.CallbackEvent);
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }
    }
}