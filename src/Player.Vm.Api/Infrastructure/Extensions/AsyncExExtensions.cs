// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Nito.AsyncEx;

namespace Player.Vm.Api.Infrastructure.Extensions
{
    public static class AsyncExExtensions
    {
        public static async Task<bool> WaitAsync(this AsyncAutoResetEvent mEvent, TimeSpan timeout, CancellationToken token = default)
        {
            var timeOut = new CancellationTokenSource(timeout);
            var comp = CancellationTokenSource.CreateLinkedTokenSource(timeOut.Token, token);

            try
            {
                await mEvent.WaitAsync(comp.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException e)
            {
                if (token.IsCancellationRequested)
                    throw; //Forward OperationCanceledException from external Token
                return false; //Here the OperationCanceledException was raised by Timeout
            }
        }
    }
}
