// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Crucible.Api.Testing;

/// <summary>
/// What <see cref="ApiTestBase{TContext}"/> needs from the application's factory. A
/// <c>WebApplicationFactory&lt;T&gt;</c> satisfies it with its own public <c>CreateClient()</c>, so the
/// factory only has to name the interface.
/// </summary>
public interface ITestHttpHost
{
    /// <summary>A client over the in-process <c>TestServer</c>.</summary>
    HttpClient CreateClient();
}

/// <summary>The JSON options responses are read with.</summary>
public static class TestJson
{
    /// <summary>
    /// Web defaults plus the string enum converter. Reading with it accepts enums as names and as
    /// numbers, so it reads what both the minimal-API and the MVC Crucible APIs write.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>
/// The core of the application's <c>ApiTestBase</c>: tests that drive the application over HTTP, through
/// the real routes, the real middleware, the real claims transformer and the real handlers, over a
/// database no other test can see.
/// </summary>
/// <remarks>
/// <para>
/// One host serves the run (or the class, for a per-class factory) and each test owns one database. The
/// two are joined by a session id this class registers with <see cref="TestDatabaseScope"/> and puts on
/// every request its clients send.
/// </para>
/// <para>
/// A request runs in its own scope with its own context, so what a test reads through
/// <see cref="DatabaseTestBase{TContext}.Db"/> after acting comes from a change tracker that never saw the
/// write. Re-read through <see cref="DatabaseTestBase{TContext}.NewContext"/>.
/// </para>
/// <para>
/// The application's <c>ApiTestBase</c> derives from this and adds the actors (<c>Root</c>,
/// <c>RootClient</c>, <c>Actor()</c>, <c>Client(actor)</c>), which are app-specific.
/// </para>
/// </remarks>
public abstract class ApiTestBase<TContext>(ITestDatabaseSessionSource<TContext> sessions, ITestHttpHost host)
    : DatabaseTestBase<TContext>(sessions)
    where TContext : DbContext
{
    private readonly Dictionary<Guid, HttpClient> _clients = [];
    private readonly Guid _sessionId = Guid.NewGuid();
    private HttpClient _unauthenticated;

    /// <summary>
    /// A client acting as the user <paramref name="userId"/>, named <paramref name="name"/>. Cached, so
    /// repeated calls share one client and its headers. The user's permissions are whatever the rows the
    /// test seeded grant through the real claims transformer.
    /// </summary>
    protected HttpClient ClientFor(Guid userId, string name)
    {
        if (!_clients.TryGetValue(userId, out var client))
        {
            client = CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());

            if (name is not null)
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name);
            }

            _clients.Add(userId, client);
        }

        return client;
    }

    /// <summary>A client carrying no identity, whose requests to an <c>/api/</c> route are answered with 401.</summary>
    protected HttpClient Client() => _unauthenticated ??= CreateClient();

    /// <summary>
    /// Asserts <paramref name="response"/> succeeded and returns its body. The failure message carries the
    /// status and the body, which is where a 500's detail is.
    /// </summary>
    protected static async Task<TValue> ReadAsync<TValue>(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail(
                $"Expected success from {Describe(response)}, got {(int)response.StatusCode} " +
                $"{response.StatusCode}: {await Body(response)}");
        }

        return await response.Content.ReadFromJsonAsync<TValue>(TestJson.Options, Ct);
    }

    /// <summary>
    /// As <see cref="ReadAsync{TValue}(HttpResponseMessage)"/>, reading with <paramref name="options"/>:
    /// for an application whose <c>AddJsonOptions</c> adds converters <see cref="TestJson.Options"/> lacks
    /// (alloy.api's <c>""</c> for a null <c>Guid?</c>). The app's <c>ApiTestBase</c> keeps those options
    /// in one field and passes them, so every test reads as the application writes.
    /// </summary>
    protected static async Task<TValue> ReadAsync<TValue>(HttpResponseMessage response, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(options);

        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail(
                $"Expected success from {Describe(response)}, got {(int)response.StatusCode} " +
                $"{response.StatusCode}: {await Body(response)}");
        }

        return await response.Content.ReadFromJsonAsync<TValue>(options, Ct);
    }

    /// <summary>Asserts the response status, naming the body when it is not the expected one.</summary>
    protected static async Task AssertStatus(HttpStatusCode expected, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode != expected)
        {
            Assert.Fail(
                $"Expected {(int)expected} {expected} from {Describe(response)}, got " +
                $"{(int)response.StatusCode} {response.StatusCode}: {await Body(response)}");
        }
    }

    /// <summary>
    /// Asserts the response is a <c>ProblemDetails</c> with <paramref name="expected"/> as its status, the
    /// shape the application's exception middleware answers a handled exception with, and returns it. A
    /// 500's <c>Detail</c> is the exception message, which is what says which failure was reached.
    /// </summary>
    protected static async Task<ProblemDetails> AssertProblem(HttpStatusCode expected, HttpResponseMessage response)
    {
        await AssertStatus(expected, response);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        return await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options, Ct);
    }

    /// <summary>
    /// Asserts the response is the body an MVC <c>JsonExceptionFilter</c> answers an exception with: a
    /// <c>ProblemDetails</c> sent as <c>application/json</c> (a <c>JsonResult</c>, not
    /// <c>application/problem+json</c>), carrying <paramref name="expected"/> as its status, and returns it.
    /// alloy.api, cite.api and steamfitter.api answer this way; a 500's <c>Detail</c> is the exception
    /// message, which is what says which throw was reached.
    /// </summary>
    protected static async Task<ProblemDetails> AssertJsonError(HttpStatusCode expected, HttpResponseMessage response)
    {
        var problem = await AssertJsonError<ProblemDetails>(expected, response);
        Assert.Equal((int)expected, problem.Status);

        return problem;
    }

    /// <summary>
    /// As <see cref="AssertJsonError(HttpStatusCode, HttpResponseMessage)"/>, for a filter that writes its own
    /// error type (cite.api's and blueprint.api's <c>ApiError</c>). The caller asserts its status member.
    /// </summary>
    protected static async Task<TError> AssertJsonError<TError>(HttpStatusCode expected, HttpResponseMessage response)
    {
        await AssertStatus(expected, response);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        return await response.Content.ReadFromJsonAsync<TError>(TestJson.Options, Ct);
    }

    private static string Describe(HttpResponseMessage response) =>
        $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}";

    private static async Task<string> Body(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        return string.IsNullOrWhiteSpace(body) ? "(empty body)" : body;
    }

    private HttpClient CreateClient()
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestDatabaseScope.HeaderName, _sessionId.ToString());

        return client;
    }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        TestDatabaseScope.Register(_sessionId, Session);
    }

    public override async ValueTask DisposeAsync()
    {
        // Released first: a request that outlives its test then fails naming the header it could not
        // route, rather than reaching a database being torn down underneath it.
        TestDatabaseScope.Release(_sessionId);

        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _unauthenticated?.Dispose();

        await base.DisposeAsync();
    }
}
