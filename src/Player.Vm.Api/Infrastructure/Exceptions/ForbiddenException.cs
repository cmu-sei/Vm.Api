// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Text.RegularExpressions;

namespace Player.Vm.Api.Infrastructure.Exceptions
{
    public class ForbiddenException : Exception, IApiException
    {
        public ForbiddenException()
            : base("Insufficient Permissions")
        {
        }

        public ForbiddenException(string message)
            : base(message)
        {
        }

        public HttpStatusCode GetStatusCode()
        {
            return HttpStatusCode.Forbidden;
        }
    }
}
