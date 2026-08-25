// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Infrastructure.Options;
// Aliased: both AutoMapper and HealthChecks.UI also export a ServiceCollectionExtensions.
using ApiExtensions = Player.Vm.Api.Infrastructure.Extensions.ServiceCollectionExtensions;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// Guest file transfers used to build an HttpClient and HttpClientHandler per call inside
/// VsphereService, which is scoped - so every request leaked a connection pool and no transfer ever
/// picked up a DNS change for a host that had moved. They now go through a named client.
///
/// These tests exist because that move is silently reversible in two ways the compiler cannot see: a
/// mismatch between the name registered and the name requested resolves to an unconfigured default
/// client (100 second timeout, full certificate validation), and the timeout and certificate settings
/// that used to be applied at the call site now live in the registration, where nothing else reads
/// them. Both would surface only in the deployments that need them - large transfers, or hosts with
/// self-signed certificates.
/// </summary>
public class GuestFileClientRegistrationTests
{
    private static ServiceProvider Provider(Action<VsphereOptions> configure)
    {
        var services = new ServiceCollection();

        services.AddOptions();
        services.Configure(configure);
        ApiExtensions.AddApiClients(
            services,
            identityClientOptions: new IdentityClientOptions(),
            clientOptions: new ClientOptions(),
            isoUploadOptions: new IsoUploadOptions());

        return services.BuildServiceProvider();
    }

    private static HttpClient GuestFileClient(Action<VsphereOptions> configure) =>
        Provider(configure)
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(ApiExtensions.GuestFileClientName);

    /// <summary>
    /// Walks past the factory's own DelegatingHandler wrappers to the handler that actually opens the
    /// connection, which is where certificate validation is decided.
    /// </summary>
    private static HttpMessageHandler PrimaryHandler(ServiceProvider provider)
    {
        var handler = provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(ApiExtensions.GuestFileClientName);

        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        return handler;
    }

    // The name has to match what VsphereService asks for. A typo either side yields a default client
    // whose timeout is HttpClient's 100 seconds, so the timeout doubles as the identity check.
    [Fact]
    public void GuestFileClient_TakesItsTimeoutFromVsphereOptions()
    {
        var client = GuestFileClient(x => x.GuestFileTransferTimeoutMinutes = 7);

        Assert.Equal(TimeSpan.FromMinutes(7), client.Timeout);
    }

    // Zero or less means "no limit", matching what the per-call code did. A multi-gigabyte upload to a
    // slow datastore is a legitimate reason to turn the timeout off entirely.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GuestFileClient_TreatsANonPositiveTimeoutAsNoLimit(int configured)
    {
        var client = GuestFileClient(x => x.GuestFileTransferTimeoutMinutes = configured);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    // ESXi hosts commonly present a self-signed certificate, and the transfer URL points at the host
    // rather than at vCenter. Without this the upload fails at the TLS handshake.
    [Fact]
    public void GuestFileClient_SkipsCertificateValidationWhenConfiguredTo()
    {
        var handler = Assert.IsType<HttpClientHandler>(
            PrimaryHandler(Provider(x => x.SkipGuestFileCertificateValidation = true)));

        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
    }

    // And the default stays strict - the opt-out has to be asked for.
    [Fact]
    public void GuestFileClient_ValidatesCertificatesByDefault()
    {
        var handler = Assert.IsType<HttpClientHandler>(
            PrimaryHandler(Provider(_ => { })));

        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }
}
