// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NSubstitute.ClearExtensions;
using Player.Api.Client;
using Player.Vm.Api.Tests.Support;

namespace Player.Vm.Api.Tests.Features.Callbacks;

/// <summary>
/// The webhook callback endpoint in process. player.api posts here when a view is created or deleted, so
/// this is the one route on the API whose caller is another service rather than a user.
///
/// Three things are worth holding still. It is the only route behind the privileged authorization policy,
/// and the whole point of that policy is that an ordinary user token does not satisfy it. It must store
/// the event before acknowledging it, because the acknowledgement is what stops player.api retrying and
/// the queue that does the work is in memory. And the enum in the body arrives as a name, because that is
/// what the sender serializes.
/// </summary>
/// <remarks>
/// The machine-to-machine caller is an actor whose requests name their own scopes through the shared
/// <see cref="TestAuthHandler.ScopeHeader"/>: the ordinary ones plus <c>Authorization:PrivilegedScope</c>.
/// <c>Root</c>'s token carries only the ordinary ones.
/// </remarks>
public class CallbacksEndpointTests(DatabaseFixture fixture, VmAppFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmAppFactory>
{
    internal const string Route = "/api/callback";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        Factory.Callbacks.ClearSubstitute();
    }

    /// <summary>
    /// The 202 means "stored, will send" - so the row has to be there, and the same row has to be the one
    /// handed to the queue. The background service removes what it processes by identity, so a detached
    /// copy without the generated id would be sent and then never cleared.
    /// </summary>
    [Fact]
    public async Task Handle_Returns202AndStoresTheEventAndQueuesIt()
    {
        var response = await Post(Payload("ViewCreated", "{\"viewId\":\"x\"}"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var context = NewContext();
        var stored = Assert.Single(context.WebhookEvents);
        Assert.Equal("{\"viewId\":\"x\"}", stored.Payload);

        Assert.Equal(stored.Id, Queued().Id);
    }

    /// <summary>
    /// The sender writes the enum as its name, and what makes that bind is the
    /// <c>JsonStringEnumConverter</c> the Player.Api.Client package declares on <c>EventType</c> itself -
    /// not the one Startup adds, which this endpoint's body does not go through. So this pins a contract
    /// held by a package this repository does not own: a regenerated client without that attribute would
    /// leave every callback rejected, and nothing else here would notice.
    ///
    /// Posted as raw JSON rather than through the application's own serializer options, which would
    /// follow the wire format wherever it went.
    /// </summary>
    [Fact]
    public async Task Handle_BindsTheEventTypeFromItsName()
    {
        var response = await Post(Payload("ViewDeleted", "{}"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var context = NewContext();
        Assert.Equal(EventType.ViewDeleted, Assert.Single(context.WebhookEvents).Type);
    }

    // Two callbacks are two rows. player.api sends one per event and does not deduplicate.
    [Fact]
    public async Task Handle_ForASecondEvent_StoresBothAndQueuesBoth()
    {
        await Post(Payload("ViewCreated", "{\"first\":true}"));
        await Post(Payload("ViewDeleted", "{\"second\":true}"));

        await using var context = NewContext();
        Assert.Equal(2, context.WebhookEvents.Count());

        await Factory.Callbacks.Received(2).AddEvent(Arg.Any<WebhookEvent>());
    }

    [Fact]
    public async Task Handle_with_an_existing_id_answers_a_server_error_and_keeps_the_event()
    {
        var id = System.Guid.NewGuid();
        var original = new WebhookEvent
        {
            Id = id,
            Type = EventType.ViewCreated,
            Timestamp = new System.DateTimeOffset(2026, 1, 1, 12, 0, 0, System.TimeSpan.Zero),
            Payload = "original"
        };
        await Seed(original);
        using var content = new StringContent(
            $"{{\"id\":\"{id}\",\"type\":\"ViewDeleted\",\"timestamp\":\"2026-01-01T12:00:00Z\",\"payload\":\"replacement\"}}",
            System.Text.Encoding.UTF8, "application/json");

        var response = await Post(content);

        var problem = await AssertJsonError(HttpStatusCode.InternalServerError, response);
        Assert.Equal("An error occurred while saving the entity changes. See the inner exception for details.", problem.Detail);
        await using var context = NewContext();
        var stored = Assert.Single(context.WebhookEvents);
        Assert.Equal(id, stored.Id);
        Assert.Equal(EventType.ViewCreated, stored.Type);
        Assert.Equal("original", stored.Payload);
        Assert.Equal(original.Timestamp, stored.Timestamp);
        await Factory.Callbacks.DidNotReceive().AddEvent(Arg.Any<WebhookEvent>());
    }

    [Fact]
    public async Task Handle_stores_and_queues_an_event_type_outside_the_enum()
    {
        using var content = new StringContent("{\"type\":999,\"timestamp\":\"2026-01-01T12:00:00Z\",\"payload\":\"{}\"}",
            System.Text.Encoding.UTF8, "application/json");

        var response = await Post(content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var context = NewContext();
        var stored = Assert.Single(context.WebhookEvents);
        Assert.Equal((EventType)999, stored.Type);
        Assert.Equal(stored.Id, Queued().Id);
        Assert.Equal((EventType)999, Queued().Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Handle_with_no_bound_event_answers_a_server_error_and_stores_nothing(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await Post(content);

        var problem = await AssertJsonError(HttpStatusCode.InternalServerError, response);
        Assert.Equal("A server error occurred.", problem.Title);
        Assert.Equal("Value cannot be null. (Parameter 'entity')", problem.Detail);
        await using var context = NewContext();
        Assert.Empty(context.WebhookEvents);
        await Factory.Callbacks.DidNotReceive().AddEvent(Arg.Any<WebhookEvent>());
    }

    [Fact]
    public async Task Handle_Unauthenticated_Is401()
    {
        var response = await Client().PostAsync(Route, Payload("ViewCreated", "{}"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// An ordinary user token authenticates and carries every scope the rest of the API asks for, and is
    /// still refused here. This is the whole reason the privileged policy exists: the callback endpoint
    /// acts on a view's behalf without any of the team permission checks the other routes run.
    /// </summary>
    [Fact]
    public async Task Handle_WithoutThePrivilegedScope_Is403AndStoresNothing()
    {
        var response = await RootClient.PostAsync(Route, Payload("ViewCreated", "{}"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.WebhookEvents);

        await Factory.Callbacks.DidNotReceive().AddEvent(Arg.Any<WebhookEvent>());
    }

    #region Helpers

    private TestActor _service;

    /// <summary>The caller player.api is: its requests carry the privileged scope as well as the ordinary ones.</summary>
    private async Task<HttpClient> ServiceClient()
    {
        if (_service is null)
        {
            _service = await Actor().WithName("player-api").SeedAsync();
            Client(_service).DefaultRequestHeaders.Add(
                TestAuthHandler.ScopeHeader, $"{VmAppFactory.AuthorizationScope} {TestConfiguration.PrivilegedScope}");
        }

        return Client(_service);
    }

    private async Task<HttpResponseMessage> Post(HttpContent content) =>
        await (await ServiceClient()).PostAsync(Route, content, Ct);

    /// <summary>
    /// The body as player.api sends it: the enum as a name, and the payload as a JSON string rather than
    /// an object, which is what its own serializer produces for an <c>object</c> property.
    /// </summary>
    internal static StringContent Payload(string type, string payload) =>
        new(
            $"{{\"type\":\"{type}\",\"timestamp\":\"2026-08-25T00:00:00Z\"," +
            $"\"payload\":{System.Text.Json.JsonSerializer.Serialize(payload)}}}",
            System.Text.Encoding.UTF8,
            "application/json");

    private WebhookEvent Queued() =>
        Factory.Callbacks.ReceivedCalls()
            .Select(x => x.GetArguments()[0])
            .Cast<WebhookEvent>()
            .Single();

    #endregion
}
