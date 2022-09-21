// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.


using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Normalize scope claims to handle array or single string
        /// </summary>
        public static ClaimsPrincipal NormalizeScopeClaims(this ClaimsPrincipal principal)
        {
            var identities = new List<ClaimsIdentity>();

            foreach (var id in principal.Identities)
            {
                var identity = new ClaimsIdentity(id.AuthenticationType, id.NameClaimType, id.RoleClaimType);

                foreach (var claim in id.Claims)
                {
                    if (claim.Type == "scope")
                    {
                        if (claim.Value.Contains(' '))
                        {
                            var scopes = claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                            foreach (var scope in scopes)
                            {
                                identity.AddClaim(new Claim("scope", scope, claim.ValueType, claim.Issuer));
                            }
                        }
                        else
                        {
                            identity.AddClaim(claim);
                        }
                    }
                    else
                    {
                        identity.AddClaim(claim);
                    }
                }

                identities.Add(identity);
            }

            return new ClaimsPrincipal(identities);
        }
    }
}
