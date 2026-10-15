// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crucible.Api.Testing;

/// <summary>
/// Stands in for the identity provider, minting the identity a validated token would have produced.
/// </summary>
/// <remarks>
/// <para>
/// This replaces only token validation. Everything downstream is real: the application's claims
/// transformer runs on the identity this mints and derives every permission claim from the database,
/// so what an actor may do is decided by the rows a test seeds.
/// </para>
/// <para>
/// The <c>scope</c> claims come from <c>Authorization:AuthorizationScope</c>, the key every Crucible API
/// reads, because each one's default policy has a <c>RequireClaim("scope", x)</c> for the configured
/// scopes. Without them every request would be a 403. A request may name its own (<see cref="ScopeHeader"/>),
/// as a machine-to-machine caller's token carries different scopes from a user's.
/// </para>
/// <para>
/// Everything else a token carries is opt-in, so a repo that needs none of it sees no difference: an
/// <c>email</c> claim from <see cref="EmailHeader"/>; an <c>iss</c> claim on every identity from
/// <see cref="IssuerKey"/>; and, with <see cref="UserFromBearerKey"/> true, the user read from
/// <c>Authorization: Bearer &lt;user id&gt;</c> when <see cref="UserHeader"/> is absent, for the code
/// paths that promote a <c>?bearer=</c> or <c>access_token</c> query parameter into that header (SignalR
/// connections). <see cref="UserHeader"/> wins when both are present, and a bearer value that is not a
/// Guid counts as no identity, so a factory that gives every client a fixed token for the application to
/// forward still has an anonymous client.
/// </para>
/// <para>
/// Register it under <see cref="SchemeName"/>, or under the name the application's own scheme has
/// (<c>Bearer</c>) where a hub or controller names that scheme in <c>[Authorize(AuthenticationSchemes =
/// ...)]</c>; the ticket carries the name it was registered under.
/// </para>
/// </remarks>
internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// The scheme's name. Not <c>Scheme</c>, which the base class already uses for the registration
    /// this handler was resolved for.
    /// </summary>
    public const string SchemeName = "Test";

    /// <summary>The user id, which becomes the <c>sub</c> claim. Absent means unauthenticated.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>
    /// The <c>name</c> claim. The claims services of the Crucible APIs write it to the user row when they
    /// create one.
    /// </summary>
    public const string NameHeader = "X-Test-Name";

    /// <summary>The <c>email</c> claim, optional (blueprint.api matches it against an invitation's domain).</summary>
    public const string EmailHeader = "X-Test-Email";

    /// <summary>
    /// The request's <c>scope</c> claims, space-separated, in place of
    /// <c>Authorization:AuthorizationScope</c> for that request alone. Optional.
    /// </summary>
    public const string ScopeHeader = "X-Test-Scope";

    /// <summary>Configuration key: the <c>iss</c> claim every identity carries. Unset means none.</summary>
    public const string IssuerKey = "TestAuthentication:Issuer";

    /// <summary>
    /// Configuration key: <c>true</c> reads the user from <c>Authorization: Bearer &lt;user id&gt;</c>
    /// when <see cref="UserHeader"/> is absent. Unset means off.
    /// </summary>
    public const string UserFromBearerKey = "TestAuthentication:UserFromBearer";

    private const string BearerPrefix = "Bearer ";

    private readonly string[] _scopes;
    private readonly string _issuer;
    private readonly bool _userFromBearer;

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _scopes = Scopes(configuration["Authorization:AuthorizationScope"]);
        _issuer = configuration[IssuerKey];
        _userFromBearer = string.Equals(configuration[UserFromBearerKey], "true", StringComparison.OrdinalIgnoreCase);
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user) && !TryBearerUser(out user))
        {
            // NoResult rather than Fail, so the pipeline challenges and the response is a 401. This is
            // what makes an unauthenticated request testable.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!Guid.TryParse(user.ToString(), out var userId))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"{UserHeader} is not a Guid: '{user}'."));
        }

        List<Claim> claims = [new("sub", userId.ToString())];

        if (Request.Headers.TryGetValue(NameHeader, out var name))
        {
            claims.Add(new Claim("name", name.ToString()));
        }

        if (Request.Headers.TryGetValue(EmailHeader, out var email))
        {
            claims.Add(new Claim("email", email.ToString()));
        }

        if (!string.IsNullOrEmpty(_issuer))
        {
            claims.Add(new Claim("iss", _issuer));
        }

        var scopes = Request.Headers.TryGetValue(ScopeHeader, out var requested) ? Scopes(requested.ToString()) : _scopes;
        claims.AddRange(scopes.Select(scope => new Claim("scope", scope)));

        // The name this handler was registered under, which is SchemeName unless the factory registered it
        // under the application's own scheme name.
        var scheme = Scheme?.Name ?? SchemeName;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, scheme)));
    }

    /// <summary>
    /// The user from <c>Authorization: Bearer &lt;id&gt;</c>, when the host opted in and the value is a
    /// Guid. Anything else is no identity rather than a failure: the factory may send every client a fixed
    /// token for the application to forward.
    /// </summary>
    private bool TryBearerUser(out Microsoft.Extensions.Primitives.StringValues user)
    {
        user = default;

        if (!_userFromBearer)
        {
            return false;
        }

        var authorization = Request.Headers.Authorization.ToString();

        if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(authorization[BearerPrefix.Length..].Trim(), out _))
        {
            return false;
        }

        user = authorization[BearerPrefix.Length..].Trim();
        return true;
    }

    private static string[] Scopes(string value) =>
        (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
