// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Text.RegularExpressions;

namespace Player.Vm.Api.Infrastructure.Exceptions
{
    public class EntityNotFoundException<T> : Exception, IApiException where T : class
    {
        private string _message { get; set; }

        public EntityNotFoundException()
            : base()
        {
        }

        public EntityNotFoundException(string message)
            : base(message)
        {
            _message = message;
        }

        public override string Message
        {
            get
            {
                if (!string.IsNullOrEmpty(_message))
                    return _message;

                var message = $"{(typeof(T).Name)} not found";

                // Add spaces between words
                return Regex.Replace(message, "([a-z](?=[A-Z]|[0-9])|[A-Z](?=[A-Z][a-z]|[0-9])|[0-9](?=[^0-9]))", "$1 ");
            }
        }

        public HttpStatusCode GetStatusCode()
        {
            return HttpStatusCode.NotFound;
        }
    }
}
