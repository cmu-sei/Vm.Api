// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api registers no IAuthorizationHandler of its own, so there is no framework authorization service to
// build here: Startup's two scope policies are exercised over HTTP (HttpHarnessTests, CallbacksEndpointTests).
// The app's authorization service is PlayerService, which answers each permission question from
// player.api's claims; PlayerServiceAuthorizationTests builds it through CreatePlayerService.

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Player.Api.Client;
using Player.Vm.Api.Domain.Services;

namespace Player.Vm.Api.Tests.Support;

/// <summary>The authorization stack wired as production wires it, for testing it without a host.</summary>
public static class AuthorizationHarness
{
    /// <summary>
    /// The production <see cref="PlayerService"/>, answering for <paramref name="user"/> from what
    /// <paramref name="client"/> (the generated player.api client) returns.
    /// </summary>
    public static PlayerService CreatePlayerService(
        ClaimsPrincipal user,
        IPlayerApiClient client,
        IViewService views,
        IMemoryCache cache = null)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };

        return new PlayerService(accessor, client, views, cache ?? new MemoryCache(new MemoryCacheOptions()));
    }

    /// <summary>
    /// Runs a requirement through a handler directly and returns the resulting context.
    /// </summary>
    public static async Task<AuthorizationHandlerContext> HandleAsync<TRequirement>(
        IAuthorizationHandler handler,
        TRequirement requirement,
        ClaimsPrincipal user,
        object resource = null)
        where TRequirement : IAuthorizationRequirement
    {
        ArgumentNullException.ThrowIfNull(handler);

        var context = new AuthorizationHandlerContext([requirement], user, resource);
        await handler.HandleAsync(context);

        return context;
    }
}
