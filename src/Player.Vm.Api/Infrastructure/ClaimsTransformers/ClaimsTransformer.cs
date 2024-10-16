// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Authentication;
using Player.Vm.Api.Infrastructure.Extensions;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Player.Vm.Api.Infrastructure.ClaimsTransformers
{
    class ClaimsTransformer : IClaimsTransformation
    {
        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var user = principal.NormalizeScopeClaims();
            return await Task.FromResult(user);
        }
    }
}
