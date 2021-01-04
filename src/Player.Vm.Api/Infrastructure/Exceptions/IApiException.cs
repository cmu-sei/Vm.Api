// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net;

namespace Player.Vm.Api.Infrastructure.Exceptions
{
    public interface IApiException
    {
        HttpStatusCode GetStatusCode();
    }
}
