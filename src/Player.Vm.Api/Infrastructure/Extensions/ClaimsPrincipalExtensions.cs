// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.


using System;
using System.Security.Claims;

namespace Player.Vm.Api.Infrastructure.Extensions
{
    public static class ClaimsPrincipalExtensions
    {
        public static Guid GetId(this ClaimsPrincipal principal)
        {
            return Guid.Parse(principal.FindFirst("sub")?.Value);
        }

        public static string GetName(this ClaimsPrincipal principal)
        {
            return principal.FindFirst("name")?.Value;
        }
    }
}
