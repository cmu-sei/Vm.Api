// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Player.Vm.Api.Infrastructure.BackgroundServices
{
    public interface ICallbackBackgroundService
    {
        void AddEvent(Task t);
    }

    public class CallbackBackgroundService : ICallbackBackgroundService
    {
        private ActionBlock<Task> _eventQueue;

        public CallbackBackgroundService()
        {
            _eventQueue = new ActionBlock<Task>(t => t.Start());
        }

        public void AddEvent(Task t)
        {
            _eventQueue.Post(t);
        }
    }
}