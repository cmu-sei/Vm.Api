// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api's principal carries no permission claims: its ClaimsTransformer only splits space-separated
// scope claims, and permissions come from player.api through PlayerService. So the claim shapes are the
// identity provider's own (sub, name, scope), and the typed steps are those.

using System;
using System.Collections.Generic;
using System.Security.Claims;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// Builds the principal the authorization stack and the services see for a signed-in user, for tests
/// that construct a service or hub with no request (an HTTP test's caller is a <see cref="TestActor"/>).
/// </summary>
public sealed class ClaimsPrincipalBuilder
{
    private readonly List<Claim> _claims = [];
    private Guid _userId = Guid.NewGuid();
    private string _name = "Test User";

    public Guid UserId => _userId;

    public ClaimsPrincipalBuilder WithUserId(Guid userId)
    {
        _userId = userId;
        return this;
    }

    /// <summary>The <c>name</c> claim; <c>null</c> leaves it out, as a token without a name would.</summary>
    public ClaimsPrincipalBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>One <c>scope</c> claim per scope, as the claims transformer leaves them.</summary>
    public ClaimsPrincipalBuilder WithScopes(params string[] scopes)
    {
        foreach (var scope in scopes)
        {
            _claims.Add(new Claim("scope", scope));
        }

        return this;
    }

    /// <summary>An arbitrary claim the identity provider issues, for asserting how it is read or ignored.</summary>
    public ClaimsPrincipalBuilder WithClaim(string type, string value)
    {
        _claims.Add(new Claim(type, value));
        return this;
    }

    public ClaimsPrincipal Build()
    {
        var claims = new List<Claim>(_claims) { new("sub", _userId.ToString()) };

        if (_name is not null)
        {
            claims.Add(new Claim("name", _name));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    /// <summary>An authenticated principal with no scopes, the baseline every policy must reject.</summary>
    public static ClaimsPrincipal Anonymous() => new ClaimsPrincipalBuilder().Build();
}
