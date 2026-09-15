// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IdentityModel.Client;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.HttpHandlers;
using Player.Vm.Api.Infrastructure.Options;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The handler that puts a bearer token on every outgoing request to player.api, and the reason an expired
/// token is invisible everywhere else in this application: a 401 is caught here, the token is thrown away
/// and the request is sent again.
/// </summary>
/// <remarks>
/// <para>
/// Registered on the <c>player-admin</c> and <c>identity</c> named clients in <c>Startup</c>, so it sits
/// under <c>ViewService</c>, <c>PlayerService</c> and the webhook clone path. It holds the header in a
/// field of its own, separately from <see cref="AuthenticationServiceTests"/>'s cache, so "has this handler
/// ever authenticated" and "does the service still hold a token" are two different questions and both are
/// asked here.
/// </para>
/// <para>
/// Two things a reader should know before changing it. The retry re-sends the same
/// <see cref="HttpRequestMessage"/>, which is fine for the GETs this application makes and would not be for
/// a request whose body is a one-pass stream. And the delay between retries is
/// <c>Math.Min(Math.Pow(2, attempt), MaxRetryDelaySeconds)</c>, which
/// <c>appsettings.json</c> ships as 120 - so the five waits a shipped deployment takes are 2, 4, 8, 16 and
/// 32 seconds, and a request that ends in the 401 of
/// <see cref="SendAsync_WhenReauthenticatingDoesNotHelp_ReturnsThe401AfterFiveRetries"/> has held its
/// caller for a minute first. Polly's waits are not given the request's cancellation token either, so
/// cancelling does not shorten them. These tests set the option to zero, which is what makes them
/// tests of how many attempts are made rather than of how long they take; nothing here asserts the
/// delay, and the arithmetic above is the only place it is written down.
/// </para>
/// </remarks>
public class AuthenticatingHandlerTests
{
    private const string Path = "api/views";

    private readonly TestHttpHandler _http = new();
    private readonly IAuthenticationService _auth = Substitute.For<IAuthenticationService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region The header

    /// <summary>
    /// The token's own type and value, in that order, which is what makes this a bearer token rather than a
    /// header this class decides the shape of.
    /// </summary>
    [Fact]
    public async Task SendAsync_SendsTheTokenAsTheAuthorizationHeader()
    {
        Issues(await Token("token-1"));
        _http.Answers(Path, HttpStatusCode.OK);

        var response = await Client().GetAsync(Path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer token-1", Assert.Single(_http.Sent).Authorization);
    }

    /// <summary>
    /// The header is built once and reused, so a second request does not go back to the authentication
    /// service at all. Together with <c>AuthenticationService</c> holding its token for the life of the
    /// process, this is what makes the steady state one token request per deployment.
    /// </summary>
    [Fact]
    public async Task SendAsync_ForASecondRequest_ReusesTheHeaderWithoutAskingAgain()
    {
        Issues(await Token("token-1"));
        _http.Answers(Path, HttpStatusCode.OK);
        var client = Client();

        await client.GetAsync(Path, Ct);
        await client.GetAsync(Path, Ct);

        Assert.Equal<string>(["Bearer token-1", "Bearer token-1"], _http.Sent.Select(x => x.Authorization));
        _auth.Received(1).GetToken(Arg.Any<CancellationToken>());
    }

    #endregion

    #region The 401 retry

    /// <summary>
    /// The whole point of the class. A 401 means the token this handler holds is no longer good, so the
    /// service's copy is invalidated - otherwise it would hand back the same one - and the request is sent
    /// again with what comes back. The caller sees only the second answer.
    /// </summary>
    [Fact]
    public async Task SendAsync_OnA401_InvalidatesTheTokenAndRetriesWithTheNewOne()
    {
        Issues(await Token("stale"), await Token("fresh"));
        _http.AnswersOnce(Path, HttpStatusCode.Unauthorized);
        _http.Answers(Path, HttpStatusCode.OK);

        var response = await Client().GetAsync(Path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal<string>(["Bearer stale", "Bearer fresh"], _http.Sent.Select(x => x.Authorization));
        _auth.Received(1).InvalidateToken();
    }

    /// <summary>
    /// When re-authenticating does not help - the credentials are wrong, or the token is fine and the caller
    /// simply is not allowed - the 401 is returned rather than swallowed or thrown, after six attempts in
    /// all. <c>result.Result</c> is null on a handled result, and returning <c>FinalHandledResult</c> is
    /// what turns the exhausted policy back into an ordinary response.
    /// </summary>
    /// <remarks>
    /// Six requests, and five token renewals, for one call the caller was never going to be allowed to make.
    /// That is the cost of not being able to tell a revoked token from a forbidden request; player.api
    /// answers 403 rather than 401 for the latter, which is what keeps this rare.
    /// </remarks>
    [Fact]
    public async Task SendAsync_WhenReauthenticatingDoesNotHelp_ReturnsThe401AfterFiveRetries()
    {
        Issues(await Token("token-1"));
        _http.Answers(Path, HttpStatusCode.Unauthorized);

        var response = await Client().GetAsync(Path, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(6, _http.Sent.Count);
        _auth.Received(5).InvalidateToken();
    }

    #endregion

    #region When there is no token to send

    /// <summary>
    /// A token response that is an error leaves the header unset, and the request goes out unauthenticated
    /// rather than not at all. player.api answers 401, the retry runs, and the deployment's logs say
    /// <c>Error in AuthenticatingHandler</c> - which is the only place the real cause appears.
    /// </summary>
    /// <remarks>
    /// The request being sent anyway is deliberate enough to rely on: it is the same code path as a
    /// deployment with no identity configuration at all, where every outgoing call fails with a 401 rather
    /// than with something naming the token. Left as it is, but the log line is the thing to look for.
    /// </remarks>
    [Fact]
    public async Task SendAsync_WhenTheTokenIsAnError_SendsWithoutAuthorization()
    {
        Issues(await ErrorToken());
        _http.Answers(Path, HttpStatusCode.OK);

        var response = await Client().GetAsync(Path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(_http.Sent).Authorization);
    }

    /// <summary>
    /// And because the header is still null afterwards, the next request tries to authenticate again. So an
    /// identity provider that is down for one request and up for the next costs one unauthenticated call,
    /// not a process restart.
    /// </summary>
    [Fact]
    public async Task SendAsync_AfterAnErrorToken_TriesToAuthenticateAgain()
    {
        Issues(await ErrorToken(), await Token("token-1"));
        _http.Answers(Path, HttpStatusCode.OK);
        var client = Client();

        await client.GetAsync(Path, Ct);
        await client.GetAsync(Path, Ct);

        Assert.Equal<string>([null, "Bearer token-1"], _http.Sent.Select(x => x.Authorization));
        _auth.Received(2).GetToken(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The one case this handler does not survive: a null token response, which
    /// <c>Authenticate</c> dereferences for <c>IsError</c> before anything else. Every outgoing request
    /// fails, and with a <see cref="NullReferenceException"/> rather than with anything naming
    /// authentication.
    /// </summary>
    /// <remarks>
    /// Characterized as a hazard, not as a contract. The only producer of a null here is
    /// <c>AuthenticationService.RenewToken</c>'s catch-all, and
    /// <c>AuthenticationServiceTests.GetToken_WhenTheProviderCannotBeReached_ReturnsAnErrorResponseAndNotNull</c>
    /// is the evidence that IdentityModel turns transport failures into error responses instead - so the
    /// pairing is what keeps this out of production, and it is one refactor of either class away from being
    /// reachable. A substituted <c>IAuthenticationService</c> is the only way to reach it from a test, which
    /// is itself the finding: nothing else in the suite could have found it.
    /// </remarks>
    [Fact]
    public async Task SendAsync_WhenNoTokenCanBeHad_ThrowsRatherThanSending()
    {
        _auth.GetToken(Arg.Any<CancellationToken>()).Returns((TokenResponse)null);

        await Assert.ThrowsAsync<NullReferenceException>(() => Client().GetAsync(Path, Ct));

        Assert.Empty(_http.Sent);
    }

    #endregion

    #region Arrangement

    /// <summary>
    /// An <see cref="HttpClient"/> over the handler under test, with the substituted transport under it -
    /// which is how <c>Startup</c> assembles it for the named clients.
    /// </summary>
    private HttpClient Client() =>
        new(
            new AuthenticatingHandler(
                _auth,
                new IdentityClientOptions { MaxRetryDelaySeconds = 0 },
                Substitute.For<ILogger<AuthenticatingHandler>>())
            {
                InnerHandler = _http,
            })
        {
            BaseAddress = new Uri("https://player.test.local/"),
        };

    /// <summary>What the authentication service hands over, in order for as many calls as are listed.</summary>
    private void Issues(TokenResponse first, params TokenResponse[] rest) =>
        _auth.GetToken(Arg.Any<CancellationToken>()).Returns(first, rest);

    /// <summary>
    /// A real <see cref="TokenResponse"/>, built the only way one can be: from the response an identity
    /// provider would have sent. Its interesting properties are read-only and computed from the parsed body.
    /// </summary>
    private static Task<TokenResponse> Token(string accessToken) =>
        Response(
            HttpStatusCode.OK,
            $"{{\"access_token\":\"{accessToken}\",\"token_type\":\"Bearer\",\"expires_in\":3600}}");

    /// <summary>A refused grant, as <c>AuthenticationService</c> would return it.</summary>
    private static Task<TokenResponse> ErrorToken() =>
        Response(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");

    private static Task<TokenResponse> Response(HttpStatusCode status, string json) =>
        ProtocolResponse.FromHttpResponseAsync<TokenResponse>(
            new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

    #endregion
}
