// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Options;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// How this API gets the token it calls player.api with: the resource owner password grant, against the
/// identity provider, with credentials out of configuration. Every outgoing request that needs
/// authorization goes through <see cref="AuthenticatingHandlerTests"/>'s handler, which gets its token
/// from here.
/// </summary>
/// <remarks>
/// <para>
/// The one decision this class makes is when to ask again, and it is worth reading closely.
/// <c>ValidateToken</c> compares <c>TokenResponse.ExpiresIn</c> - the lifetime the provider stated when it
/// issued the token, a number that never changes - against the configured <c>TokenRefreshSeconds</c>. It
/// is not a countdown, so the comparison has the same answer at second one as at second thirty thousand:
/// either the token is renewed on every single call or it is never renewed at all. Which of the two is
/// decided by configuration, and both are tested here.
/// </para>
/// <para>
/// The transport is substituted and everything above it is real, including IdentityModel's token request -
/// see <see cref="TestHttpHandler"/>. That matters more than usual here, because IdentityModel's error
/// handling is what decides the shape of what this class returns.
/// </para>
/// </remarks>
public class AuthenticationServiceTests
{
    private const string TokenUrl = "https://identity.test.local/connect/token";
    private const string TokenPath = "connect/token";

    private readonly TestHttpHandler _http = new();
    private readonly IdentityClientOptions _options = new()
    {
        TokenUrl = TokenUrl,
        ClientId = "player-vm-api",
        Scope = "player-api",
        UserName = "vm-api@test.local",
        Password = "secret",
        TokenRefreshSeconds = 900,
    };

    private readonly IAuthenticationService _service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AuthenticationServiceTests()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_http, disposeHandler: false));

        var monitor = Substitute.For<IOptionsMonitor<IdentityClientOptions>>();
        monitor.CurrentValue.Returns(_options);

        _service = new AuthenticationService(factory, monitor, Substitute.For<ILogger<AuthenticationService>>());
    }

    #region The request

    /// <summary>
    /// The grant, and every field of it. These are the values an operator sets in configuration, and a
    /// deployment where one of them stops being sent is a deployment where this API can read nothing from
    /// player.api - with the failure appearing as a 401 several layers away.
    /// </summary>
    [Fact]
    public void GetToken_RequestsThePasswordGrantWithTheConfiguredCredentials()
    {
        Issues(expiresIn: 3600);

        var token = _service.GetToken(Ct);

        Assert.Equal("token-1", token.AccessToken);
        Assert.Equal("Bearer", token.TokenType);

        var request = Assert.Single(_http.Sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(TokenPath, request.Path);

        var form = Form(request.Body);
        Assert.Equal("password", form["grant_type"]);
        Assert.Equal("player-vm-api", form["client_id"]);
        Assert.Equal("player-api", form["scope"]);
        Assert.Equal("vm-api@test.local", form["username"]);
        Assert.Equal("secret", form["password"]);
    }

    #endregion

    #region When it asks again

    /// <summary>
    /// A token whose stated lifetime is longer than the refresh threshold is held. This is the ordinary
    /// deployment: an hour's lifetime against fifteen minutes.
    /// </summary>
    [Fact]
    public void GetToken_ForATokenOutlivingTheThreshold_AsksOnce()
    {
        Issues(expiresIn: 3600);

        Assert.Equal("token-1", _service.GetToken(Ct).AccessToken);
        Assert.Equal("token-1", _service.GetToken(Ct).AccessToken);

        Assert.Single(_http.Sent);
    }

    /// <summary>
    /// And it is held for as long as the process lives, because the comparison is against a stated lifetime
    /// rather than a remaining one. Nothing here or in <c>AuthenticatingHandler</c> renews a token before it
    /// expires: the token in hand is used until a 401 comes back and
    /// <see cref="GetToken_AfterInvalidateToken_AsksAgain"/> happens.
    /// </summary>
    /// <remarks>
    /// Characterized. What makes it work in production is that <c>AuthenticatingHandler</c> treats a 401 as
    /// "get another one and retry", so an expired token costs one wasted request rather than a failure. The
    /// cost of the arrangement is that it cannot be told apart from a genuinely revoked token, and that a
    /// short-lived token means the retry on nearly every call - see
    /// <see cref="GetToken_ForATokenNotOutlivingTheThreshold_AsksEveryTime"/>.
    /// </remarks>
    [Fact]
    public void GetToken_NeverRenewsATokenItStillHolds()
    {
        Issues(expiresIn: 3600);

        for (var i = 0; i < 5; i++)
        {
            _service.GetToken(Ct);
        }

        Assert.Single(_http.Sent);
    }

    /// <summary>
    /// The other side of the same comparison: configure the identity provider to issue tokens no longer
    /// lived than the refresh threshold and every call fetches a new one, because the token is considered
    /// due for renewal from the moment it arrives.
    /// </summary>
    /// <remarks>
    /// Characterized, and the reason to state the rule as "either always or never". A provider issuing
    /// fifteen-minute tokens against the default fifteen-minute threshold puts a full token request in front
    /// of every outgoing call to player.api, serialized on this class's lock, and nothing anywhere reports
    /// it as anything other than latency.
    /// </remarks>
    [Fact]
    public void GetToken_ForATokenNotOutlivingTheThreshold_AsksEveryTime()
    {
        Issues(expiresIn: 900);

        _service.GetToken(Ct);
        _service.GetToken(Ct);
        _service.GetToken(Ct);

        Assert.Equal(3, _http.Sent.Count);
    }

    /// <summary>
    /// The only thing that does discard a held token, and the one <c>AuthenticatingHandler</c> calls on a
    /// 401. The second call must return the new token rather than the invalidated one, so the two answers
    /// here are deliberately different.
    /// </summary>
    [Fact]
    public void GetToken_AfterInvalidateToken_AsksAgain()
    {
        Issues(expiresIn: 3600, accessToken: "token-1", once: true);
        Issues(expiresIn: 3600, accessToken: "token-2");

        Assert.Equal("token-1", _service.GetToken(Ct).AccessToken);
        _service.InvalidateToken();

        Assert.Equal("token-2", _service.GetToken(Ct).AccessToken);
        Assert.Equal(2, _http.Sent.Count);
    }

    #endregion

    #region When the provider says no

    /// <summary>
    /// A refused grant - wrong password, disabled account, unknown client - comes back as a token response
    /// that is an error rather than as an exception, and the caller has to look at <c>IsError</c> to tell.
    /// <c>AuthenticatingHandler</c> does, and logs it.
    /// </summary>
    [Fact]
    public void GetToken_WhenTheProviderRefuses_ReturnsAnErrorResponse()
    {
        _http.AnswersJson(TokenPath, "{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest);

        var token = _service.GetToken(Ct);

        Assert.True(token.IsError);
        Assert.Equal("invalid_grant", token.Error);
    }

    /// <summary>
    /// An error is not held, because its <c>ExpiresIn</c> is zero and zero is never longer than the refresh
    /// threshold. So a provider that comes back up is picked up on the next call, and a provider that is
    /// down is asked once per call for as long as it is down.
    /// </summary>
    [Fact]
    public void GetToken_AfterAnError_AsksAgain()
    {
        _http.AnswersJson(TokenPath, "{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest, once: true);
        Issues(expiresIn: 3600);

        Assert.True(_service.GetToken(Ct).IsError);
        Assert.Equal("token-1", _service.GetToken(Ct).AccessToken);
    }

    /// <summary>
    /// A provider that cannot be reached at all is also an error response and not an exception: IdentityModel
    /// catches the transport failure and reports it as one, keeping the exception on the response.
    /// </summary>
    /// <remarks>
    /// Which means <c>RenewToken</c>'s <c>catch</c>, and the null it returns, are all but unreachable - and
    /// that is a good thing, because a null token is what <c>AuthenticatingHandler.Authenticate</c>
    /// dereferences without checking. This test is the evidence for that claim; see
    /// <c>AuthenticatingHandlerTests.SendAsync_WhenTheTokenIsAnError_SendsWithoutAuthorization</c> for what
    /// the handler does with the response it does get.
    /// </remarks>
    [Fact]
    public void GetToken_WhenTheProviderCannotBeReached_ReturnsAnErrorResponseAndNotNull()
    {
        _http.Throws(TokenPath);

        var token = _service.GetToken(Ct);

        Assert.NotNull(token);
        Assert.True(token.IsError);
        Assert.IsType<HttpRequestException>(token.Exception);
    }

    #endregion

    #region Arrangement

    /// <summary>What the identity provider answers, as the OAuth token response it really is.</summary>
    private void Issues(int expiresIn, string accessToken = "token-1", bool once = false) =>
        _http.AnswersJson(
            TokenPath,
            $"{{\"access_token\":\"{accessToken}\",\"token_type\":\"Bearer\",\"expires_in\":{expiresIn}}}",
            HttpStatusCode.OK,
            once);

    /// <summary>
    /// The form-encoded request body as fields. Parsed here rather than asserted as a string, so that a
    /// failure names the field that changed.
    /// </summary>
    private static Dictionary<string, string> Form(string body) =>
        body.Split('&')
            .Select(x => x.Split('=', 2))
            .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));

    #endregion
}
