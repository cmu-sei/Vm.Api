// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Authorization;

namespace Player.Vm.Api.Infrastructure.Extensions
{
    public static class AuthorizationPolicyBuilderExtensions
    {
        /// <summary>
        /// Adds a requirement that the user must have a claim with the name "scope" and the specified value.
        /// This is commonly used for OAuth2/OIDC scope-based authorization.
        /// </summary>
        /// <param name="builder">The AuthorizationPolicyBuilder to extend</param>
        /// <param name="scope">The required scope value</param>
        /// <returns>The AuthorizationPolicyBuilder for chaining</returns>
        public static AuthorizationPolicyBuilder RequireScope(this AuthorizationPolicyBuilder builder, string scope)
        {
            return builder.RequireClaim("scope", scope);
        }
    }
}
