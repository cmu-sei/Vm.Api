// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Player.Vm.Api.Infrastructure.Extensions;

namespace Player.Vm.Api.Infrastructure.Authorization
{
    public interface IIdentityResolver
    {
        ClaimsPrincipal GetClaimsPrincipal();
        Guid GetId();
    }

    public class IdentityResolver : IIdentityResolver
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAuthorizationService _authorizationService;

        public IdentityResolver(
            IHttpContextAccessor httpContextAccessor,
            IAuthorizationService authorizationService)
        {
            _httpContextAccessor = httpContextAccessor;
            _authorizationService = authorizationService;
        }

        public ClaimsPrincipal GetClaimsPrincipal()
        {
            return _httpContextAccessor?.HttpContext?.User;
        }

        public Guid GetId()
        {
            return this.GetClaimsPrincipal().GetId();
        }
    }
}
