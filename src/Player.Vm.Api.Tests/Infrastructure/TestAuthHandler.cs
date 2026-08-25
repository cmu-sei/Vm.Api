// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Player.Vm.Api.Tests.Infrastructure;

public class TestAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>Scopes granted to every authenticated test request.</summary>
    public IEnumerable<string> Scopes { get; set; } = [];
}

/// <summary>
/// Stands in for the JWT bearer handler so tests do not need an identity server. A request carrying
/// the <see cref="UserIdHeader"/> header authenticates as that user; a request without it presents no
/// credentials at all, which keeps the 401 path testable.
///
/// The scopes come from <see cref="VmApiFactory"/> rather than being hardcoded here, because Startup
/// builds its default authorization policy out of Authorization:AuthorizationScope - a principal
/// missing any one of those scope claims never reaches a controller.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<TestAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<TestAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    /// <summary>Set to the user's guid. Absent means "no credentials presented".</summary>
    public const string UserIdHeader = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserIdHeader, out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", userId.ToString()) };
        claims.AddRange(Options.Scopes.Select(x => new Claim("scope", x)));

        var identity = new ClaimsIdentity(claims, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
