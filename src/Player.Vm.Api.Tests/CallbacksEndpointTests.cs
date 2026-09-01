// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Player.Api.Client;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

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
public class CallbacksEndpointTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    private const string Route = "/api/callback";

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

    /// <summary>
    /// An ordinary user token authenticates and carries every scope the rest of the API asks for, and is
    /// still refused here. This is the whole reason the privileged policy exists: the callback endpoint
    /// acts on a view's behalf without any of the team permission checks the other routes run.
    /// </summary>
    [Fact]
    public async Task Handle_WithoutThePrivilegedScope_Is403AndStoresNothing()
    {
        var response = await Client.PostAsync(Route, Payload("ViewCreated", "{}"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = NewContext();
        Assert.Empty(context.WebhookEvents);

        await Factory.Callbacks.DidNotReceive().AddEvent(Arg.Any<WebhookEvent>());
    }

    [Fact]
    public async Task Handle_Unauthenticated_Is401()
    {
        var response = await AnonymousClient.PostAsync(Route, Payload("ViewCreated", "{}"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #region Helpers

    private Task<HttpResponseMessage> Post(HttpContent content) =>
        PrivilegedClient.PostAsync(Route, content, Ct);

    /// <summary>
    /// The body as player.api sends it: the enum as a name, and the payload as a JSON string rather than
    /// an object, which is what its own serializer produces for an <c>object</c> property.
    /// </summary>
    private static StringContent Payload(string type, string payload) =>
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
