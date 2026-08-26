// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// A substituted transport. Everything above it is production code: the generated
/// <c>PlayerApiClient</c>, IdentityModel's token request, the <c>HttpClient</c> pipeline and whatever
/// <c>DelegatingHandler</c>s are wrapped around it all run for real, and only the socket is replaced.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam the rest of the suite reaches by substituting <c>IViewService</c> and friends
/// outright. Those substitutes are right for a test about a handler that consults player.api; this is
/// what is left over - the clients themselves, which exist to turn a method call into a request and a
/// response into an answer, and where the only thing worth asserting is the request that went out and
/// what was made of what came back.
/// </para>
/// <para>
/// A request nothing stubbed throws rather than answering 404, because in this suite an unexpected
/// request is an arrangement that has drifted from the route the client builds - and a 404 would be
/// swallowed by the very error handling several of these tests are about.
/// </para>
/// </remarks>
public sealed class TestHttpHandler : HttpMessageHandler
{
    private readonly List<Rule> _rules = [];

    /// <summary>Every request that reached the transport, in order, with what it carried.</summary>
    public List<SentRequest> Sent { get; } = [];

    /// <summary>The path of each request, in order. The usual assertion about caching.</summary>
    public IEnumerable<string> Paths => Sent.Select(x => x.Path);

    /// <summary>200 with <paramref name="body"/> serialized as the client's DTOs declare themselves.</summary>
    /// <remarks>
    /// System.Text.Json, and deliberately with no options: the Player.Api.Client types carry
    /// <c>[JsonPropertyName]</c> on every property, so serializing one produces the names its own
    /// deserializer looks for. Hand-written JSON here would be a second guess at that contract.
    /// </remarks>
    public TestHttpHandler Answers(string path, object body) =>
        Add(path, HttpStatusCode.OK, JsonSerializer.Serialize(body), once: false);

    /// <summary>A body written out as it arrives on the wire, with the status the sender gave it.</summary>
    /// <remarks>
    /// For the identity provider, whose token response is not a type this repository has: what
    /// IdentityModel parses is the OAuth JSON, so that is what a test of it should be handing over.
    /// </remarks>
    public TestHttpHandler AnswersJson(
        string path, string json, HttpStatusCode status = HttpStatusCode.OK, bool once = false) =>
        Add(path, status, json, once);

    /// <summary>A status and nothing else, for the refusals.</summary>
    public TestHttpHandler Answers(string path, HttpStatusCode status) =>
        Add(path, status, string.Empty, once: false);

    /// <summary>
    /// A status the first time the path is asked for and nothing after that, so a later rule for the same
    /// path answers the retry. Rules are matched in the order they were added.
    /// </summary>
    public TestHttpHandler AnswersOnce(string path, HttpStatusCode status) =>
        Add(path, status, string.Empty, once: true);

    /// <summary>
    /// Fails the way a name that does not resolve or a refused connection fails, which is not a status
    /// code at all.
    /// </summary>
    public TestHttpHandler Throws(string path)
    {
        _rules.Add(new Rule(path, HttpStatusCode.OK, body: null, once: false));

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Path(request);

        Sent.Add(new SentRequest(
            request.Method,
            path,
            request.RequestUri.Query,
            request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

        var rule = _rules.FirstOrDefault(x => !x.Used && x.Matches(path));
        rule?.Use();

        if (rule is null)
        {
            throw new InvalidOperationException(
                $"TestHttpHandler was asked for {request.Method} {path}, which nothing stubbed. " +
                $"Stubbed: {(_rules.Count == 0 ? "nothing" : string.Join(", ", _rules.Select(x => x.Path)))}.");
        }

        if (rule.Body is null)
        {
            throw new HttpRequestException($"TestHttpHandler was told to fail {path}.");
        }

        return new HttpResponseMessage(rule.Status)
        {
            Content = new StringContent(rule.Body, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
    }

    private TestHttpHandler Add(string path, HttpStatusCode status, string body, bool once)
    {
        _rules.Add(new Rule(path, status, body, once));

        return this;
    }

    /// <summary>The path as a rule spells it: no leading slash, no query.</summary>
    private static string Path(HttpRequestMessage request) =>
        request.RequestUri.AbsolutePath.TrimStart('/');

    /// <summary>What one request carried. The body is read here because the content is disposed later.</summary>
    public sealed record SentRequest(
        HttpMethod Method, string Path, string Query, string Authorization, string Body);

    private sealed class Rule(string path, HttpStatusCode status, string body, bool once)
    {
        public string Path { get; } = path;
        public HttpStatusCode Status { get; } = status;

        /// <summary>Null means "throw instead of answering".</summary>
        public string Body { get; } = body;

        public bool Used { get; private set; }

        public void Use() => Used = once;

        /// <summary>
        /// The path, or a prefix of it when the rule ends in <c>*</c> - which is what an arrangement that
        /// does not care about the id in the route wants.
        /// </summary>
        public bool Matches(string requested) =>
            Path.EndsWith('*')
                ? requested.StartsWith(Path.TrimEnd('*'), StringComparison.Ordinal)
                : string.Equals(requested, Path.TrimStart('/'), StringComparison.Ordinal);
    }
}
