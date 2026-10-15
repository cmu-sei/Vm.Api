// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Crucible.Api.Testing;

/// <summary>
/// Answers outbound requests, for handlers that fetch over HTTP: from a table of absolute urls, and from
/// route rules tried in the order a test added them.
/// </summary>
/// <remarks>
/// <para>
/// The alternative is substituting <see cref="IHttpClientFactory"/> to return a substituted client,
/// which cannot be done — <see cref="HttpClient"/> is a concrete class with no virtual
/// <c>GetAsync</c>. Replacing the message handler is the supported seam. Everything above it (a generated
/// client, IdentityModel's token request, the <c>DelegatingHandler</c>s production wraps around it) runs
/// for real.
/// </para>
/// <para>
/// The tables are concurrent because one instance answers the whole HTTP suite
/// (the app factory's <c>OutboundHttp</c>): a test registers a url while another test's request is
/// reading, and a plain <see cref="Dictionary{TKey, TValue}"/> read during a write is undefined rather
/// than merely stale.
/// </para>
/// <para>
/// Two ways to arrange an answer. <see cref="Respond"/>, <see cref="RespondJson"/>,
/// <see cref="RespondWithStatus"/> and <see cref="RespondByThrowing"/> answer one absolute url exactly,
/// query included: what a test arranges on the run-wide <c>OutboundHttp</c>, with a url no other test
/// uses. The route rules (<c>Answers</c>, <c>AnswersJson</c>, <c>AnswersOnce</c>, <c>Throws</c>) match the
/// request's path without host or query (<c>api/views/42/teams</c>), optionally scoped to one method
/// (<c>"GET api/views/*/teams"</c>), where <c>*</c> stands for any run of characters. They are tried in the
/// order they were added, after the url table, and a rule marked once answers one request and then steps
/// aside, which is how a test arranges a sequence. A rule answers every request whose path matches, so
/// rules belong on a handler one test owns (a <c>new StubHttpMessageHandler()</c> handed to the code under
/// test, an <c>ApiTestHost</c>, a per-class factory), never on the run-wide one.
/// </para>
/// <para>
/// A request nothing arranged answers 404, so a run-wide table lists only what tests care about. A handler
/// one test owns may call <see cref="RefusesUnmatched"/> instead: such a request then throws, naming itself
/// and what was arranged, because a 404 is swallowed by the very error handling many of those tests are
/// about.
/// </para>
/// </remarks>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    /// <summary>
    /// Web defaults: camelCase names for anonymous objects, and the names a generated client's
    /// <c>[JsonPropertyName]</c> attributes give its DTOs, which is what the sibling APIs write.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, (HttpStatusCode Status, byte[] Content, string ContentType)> _responses = [];
    private readonly ConcurrentDictionary<string, Exception> _failures = [];
    private readonly Lock _recording = new();
    private readonly Lock _routing = new();

    private readonly List<string> _requests = [];
    private readonly List<SentRequest> _sent = [];
    private readonly List<Rule> _rules = [];
    private volatile bool _refusesUnmatched;

    /// <summary>Every absolute uri that was requested, in order.</summary>
    /// <remarks>
    /// A snapshot taken under the recording lock rather than the live list. The callers under test include
    /// background senders that record from their own threads, and reading a <c>List&lt;T&gt;</c> while
    /// another thread adds to it is undefined — which a test that polls this in a loop will eventually
    /// find out.
    /// </remarks>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_recording)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// The same requests with their method, headers and body, for the callers whose behaviour is in what
    /// they send rather than what they do with the answer. Also a snapshot.
    /// </summary>
    public IReadOnlyList<SentRequest> Sent
    {
        get
        {
            lock (_recording)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>The path of each request as a route rule spells it, in order. Also a snapshot.</summary>
    public IReadOnlyList<string> Paths => [.. Sent.Select(x => x.Path)];

    public StubHttpMessageHandler Respond(
        string uri,
        byte[] content,
        string contentType = "image/png",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[uri] = (status, content, contentType);
        return this;
    }

    /// <summary>
    /// Answers <paramref name="uri"/> with <paramref name="body"/> serialized as JSON with web defaults
    /// (see <see cref="Json"/>), as <c>application/json</c>. A string is serialized as a JSON string; raw
    /// JSON goes through <see cref="Respond"/> with UTF-8 bytes.
    /// </summary>
    public StubHttpMessageHandler RespondJson(string uri, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond(uri, JsonSerializer.SerializeToUtf8Bytes(body, Json), "application/json", status);

    public StubHttpMessageHandler RespondWithStatus(string uri, HttpStatusCode status)
    {
        _responses[uri] = (status, [], null);
        return this;
    }

    /// <summary>Fails the request, for the paths that handle a transport error rather than a status.</summary>
    public StubHttpMessageHandler RespondByThrowing(string uri, Exception exception)
    {
        _failures[uri] = exception;
        return this;
    }

    /// <summary>A route rule: 200 with <paramref name="body"/> serialized as <see cref="RespondJson"/> does.</summary>
    public StubHttpMessageHandler Answers(string route, object body) =>
        AddRule(route, HttpStatusCode.OK, JsonSerializer.Serialize(body, Json), once: false);

    /// <summary>A route rule answering a status and an empty body, for the refusals.</summary>
    public StubHttpMessageHandler Answers(string route, HttpStatusCode status) =>
        AddRule(route, status, string.Empty, once: false);

    /// <summary>
    /// A route rule answering a status the first time the route is asked for and nothing after that, so a
    /// later rule for the same route answers the retry.
    /// </summary>
    public StubHttpMessageHandler AnswersOnce(string route, HttpStatusCode status) =>
        AddRule(route, status, string.Empty, once: true);

    /// <summary>
    /// A route rule answering <paramref name="json"/> as it arrives on the wire, for a body that is not a
    /// type the repository has (an identity provider's discovery document and token response).
    /// </summary>
    /// <remarks>
    /// A rule that is not <paramref name="once"/> answers every request for its route, so two of them do
    /// not make a queue: the first answers both. To give two calls to one route different answers, mark the
    /// earlier one <paramref name="once"/>.
    /// </remarks>
    public StubHttpMessageHandler AnswersJson(
        string route,
        string json,
        HttpStatusCode status = HttpStatusCode.OK,
        bool once = false) =>
        AddRule(route, status, json, once);

    /// <summary>
    /// A route rule whose body is computed when the request arrives rather than when the rule is written,
    /// for a resource the test goes on to change between two reads.
    /// </summary>
    public StubHttpMessageHandler AnswersJson(string route, Func<string> json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return AddRule(new Rule(route, HttpStatusCode.OK, json, once: false));
    }

    /// <summary>
    /// A route rule that fails the way a name that does not resolve or a refused connection fails, with an
    /// <see cref="HttpRequestException"/> rather than a status.
    /// </summary>
    public StubHttpMessageHandler Throws(string route) =>
        AddRule(new Rule(route, HttpStatusCode.OK, body: null, once: false));

    /// <summary>
    /// Makes a request that neither the url table nor a route rule answers throw instead of answering 404.
    /// Only on a handler one test owns: on the run-wide one it would fail every other test's unarranged
    /// request.
    /// </summary>
    public StubHttpMessageHandler RefusesUnmatched()
    {
        _refusesUnmatched = true;
        return this;
    }

    /// <summary>
    /// The url table first, then the route rules in the order they were added, then 404 (or a throw, after
    /// <see cref="RefusesUnmatched"/>).
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri.ToString();
        var captured = await Capture(request, cancellationToken);

        // Locked because callers include background senders that run a request per queue in parallel, and
        // because Requests and Sent snapshot under the same lock.
        lock (_recording)
        {
            _requests.Add(uri);
            _sent.Add(captured);
        }

        if (_failures.TryGetValue(uri, out var failure))
        {
            throw failure;
        }

        if (_responses.TryGetValue(uri, out var stubbed))
        {
            var response = new HttpResponseMessage(stubbed.Status)
            {
                Content = new ByteArrayContent(stubbed.Content)
            };

            if (stubbed.ContentType != null)
            {
                response.Content.Headers.ContentType = new(stubbed.ContentType);
            }

            return response;
        }

        var rule = Take(request.Method, captured.Path);

        if (rule is null)
        {
            if (_refusesUnmatched)
            {
                throw new InvalidOperationException(
                    $"StubHttpMessageHandler was asked for {request.Method} {uri}, which nothing arranged. " +
                    $"Arranged: {Arranged()}.");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        // Read once: a lazily computed body may not answer the same way twice.
        var body = rule.Body;

        if (body is null)
        {
            throw new HttpRequestException($"StubHttpMessageHandler was told to fail {request.Method} {captured.Path}.");
        }

        return new HttpResponseMessage(rule.Status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };
    }

    private StubHttpMessageHandler AddRule(string route, HttpStatusCode status, string body, bool once) =>
        AddRule(new Rule(route, status, () => body, once));

    private StubHttpMessageHandler AddRule(Rule rule)
    {
        lock (_routing)
        {
            _rules.Add(rule);
        }

        return this;
    }

    /// <summary>The first rule that matches and is not spent, spending it if it answers once.</summary>
    private Rule Take(HttpMethod method, string path)
    {
        lock (_routing)
        {
            var rule = _rules.FirstOrDefault(x => !x.Spent && x.Matches(method, path));
            rule?.Use();

            return rule;
        }
    }

    private string Arranged()
    {
        List<string> arranged = [.. _responses.Keys, .. _failures.Keys];

        lock (_routing)
        {
            arranged.AddRange(_rules.Select(x => x.Pattern));
        }

        return arranged.Count == 0 ? "nothing" : string.Join(", ", arranged);
    }

    /// <summary>
    /// Copies the request, because the caller disposes it — and its content — as soon as the response is
    /// read, leaving nothing to assert on afterwards.
    /// </summary>
    private static async Task<SentRequest> Capture(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value));

        return new SentRequest(
            request.RequestUri.ToString(),
            request.Method,
            request.Content == null ? null : await request.Content.ReadAsStringAsync(ct),
            request.Content?.Headers.ContentType?.MediaType,
            headers);
    }

    private sealed class Rule
    {
        private readonly Func<string> _body;
        private readonly bool _once;

        public Rule(string pattern, HttpStatusCode status, Func<string> body, bool once)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

            Pattern = pattern;
            Status = status;
            _body = body;
            _once = once;

            // "GET some/path" scopes the rule to one method; a bare path answers any of them.
            var verb = pattern.IndexOf(' ');

            if (verb > 0)
            {
                Method = HttpMethod.Parse(pattern.AsSpan(0, verb));
                Path = pattern[(verb + 1)..].TrimStart('/');
            }
            else
            {
                Path = pattern.TrimStart('/');
            }
        }

        /// <summary>As the test wrote it, method prefix and all. What an unmatched request is told about.</summary>
        public string Pattern { get; }

        public string Path { get; }

        /// <summary>Null means "any method", which is every rule that did not name one.</summary>
        public HttpMethod Method { get; }

        public HttpStatusCode Status { get; }

        /// <summary>Null means "throw instead of answering". Read once per request that matches.</summary>
        public string Body => _body?.Invoke();

        public bool Spent { get; private set; }

        public void Use() => Spent = _once;

        /// <summary>
        /// The method, when the rule named one, and the path, where <c>*</c> stands for any run of
        /// characters anywhere in the pattern (<c>player/api/views/*/teams</c>). No <c>*</c> is an exact
        /// match; a trailing one is a prefix match.
        /// </summary>
        public bool Matches(HttpMethod method, string requested)
        {
            if (Method is not null && Method != method)
            {
                return false;
            }

            if (!Path.Contains('*'))
            {
                return string.Equals(requested, Path, StringComparison.Ordinal);
            }

            var parts = Path.Split('*');

            if (!requested.StartsWith(parts[0], StringComparison.Ordinal) ||
                !requested.EndsWith(parts[^1], StringComparison.Ordinal) ||
                requested.Length < parts.Sum(x => x.Length))
            {
                return false;
            }

            // The middle parts in order, so a/*/b/*/c cannot be satisfied out of sequence.
            var at = parts[0].Length;

            for (var i = 1; i < parts.Length - 1; i++)
            {
                var found = requested.IndexOf(parts[i], at, StringComparison.Ordinal);

                if (found < 0)
                {
                    return false;
                }

                at = found + parts[i].Length;
            }

            return at <= requested.Length - parts[^1].Length;
        }
    }
}

/// <summary>One outbound request as it left, headers and body included.</summary>
public sealed record SentRequest(
    string Uri,
    HttpMethod Method,
    string Body,
    string ContentType,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The path as a route rule spells it: no host, no leading slash, no query.</summary>
    public string Path => new System.Uri(Uri).AbsolutePath.TrimStart('/');

    /// <summary>The query string, <c>?</c> included, or empty.</summary>
    public string Query => new System.Uri(Uri).Query;

    /// <summary>The <c>Authorization</c> header as sent, or null.</summary>
    public string Authorization =>
        Headers?.FirstOrDefault(x => string.Equals(x.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// Hands out clients over one handler, for the app factory's <see cref="IHttpClientFactory"/>. Written out
/// rather than substituted, because every registration on a substitute is shared by the whole run.
/// </summary>
/// <remarks>
/// <para>
/// A substitute here has to be told what to return, and a test that says so is reconfiguring a singleton
/// while other tests are mid-request. The value it set is then the value every test gets until the next one
/// says otherwise, and NSubstitute's own bookkeeping is not built for two threads arranging the same call.
/// Both hazards are gone once the answer is fixed at construction; tests assert on the handler.
/// </para>
/// <para>
/// Replacing the factory drops the <c>AddHttpClient(name, configure)</c> delegates production registers.
/// <paramref name="baseAddresses"/> restores the one that matters to a caller requesting relative urls on
/// a named client: its base address, by client name (caster.api's <c>"gitlab"</c> client). Nothing else
/// those delegates set (headers, timeouts) is restored.
/// </para>
/// </remarks>
public sealed class StubHttpClientFactory(
    HttpMessageHandler handler,
    IReadOnlyDictionary<string, Uri> baseAddresses = null) : IHttpClientFactory
{
    /// <remarks>
    /// A client per call, and the handler outlives it: a caller that wraps its client in <c>using</c>
    /// would otherwise dispose the run's only handler and every later test would fetch nothing.
    /// </remarks>
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(handler, disposeHandler: false);

        if (name is not null && baseAddresses is not null && baseAddresses.TryGetValue(name, out var baseAddress))
        {
            client.BaseAddress = baseAddress;
        }

        return client;
    }
}
