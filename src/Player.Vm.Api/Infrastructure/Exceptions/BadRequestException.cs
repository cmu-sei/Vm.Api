// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Text.RegularExpressions;

namespace Player.Vm.Api.Infrastructure.Exceptions
{
    public class BadRequestException : Exception, IApiException
    {
        public BadRequestException()
            : base("Bad Request")
        {
        }

        public BadRequestException(string message)
            : base(message)
        {
        }

        public HttpStatusCode GetStatusCode()
        {
            return HttpStatusCode.BadRequest;
        }
    }
}
