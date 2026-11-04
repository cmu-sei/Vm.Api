// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;

namespace Player.Vm.Api.Infrastructure.Exceptions
{
    /// <summary>
    /// The request is well formed but clashes with a resource that already exists, so it can't be
    /// applied as sent.
    /// </summary>
    public class ConflictException : Exception, IApiException
    {
        public ConflictException()
            : base("Conflict")
        {
        }

        public ConflictException(string message)
            : base(message)
        {
        }

        public HttpStatusCode GetStatusCode()
        {
            return HttpStatusCode.Conflict;
        }
    }
}
