// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Vm.Api.Infrastructure.HttpHandlers;

internal class LocalhostRedirectHandler : DelegatingHandler
{
    /// <summary>
    /// Redirects wildcard URIs to localhost - workaround to support HealthChecksUI with the same configured path locally and in a container
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null) return base.SendAsync(request, cancellationToken);

        // If URI resolved against 0.0.0.0 or [::], or any other wildcard - then rewrite to loopback, keep scheme+port
        if (uri.Host is "0.0.0.0" or "[::]" or "::" or "*" or "+")
            request.RequestUri = new UriBuilder(uri) { Host = "localhost" }.Uri;

        return base.SendAsync(request, cancellationToken);
    }
}