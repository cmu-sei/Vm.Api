// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Player.Vm.Api.Domain.Vsphere.Models;
using Player.Vm.Api.Domain.Vsphere.Options;
using Player.Vm.Api.Domain.Vsphere.Services;
using Player.Vm.Api.Features.Files.Models;
using VimClient;
using Xunit;

namespace Player.Vm.Api.Tests;

public class VsphereIsoStorageGroupTests
{
    private static VsphereHost Host(string address, string group = null) => new()
    {
        Address = address,
        Username = "user",
        Password = "password",
        DsName = $"datastore-{address}",
        BaseFolder = $"folder-{address}",
        IsoStorageGroup = group
    };

    // Real VsphereService and datastore HTTP path, with only the vCenter transport substituted.
    private sealed class Storage : IDisposable
    {
        public readonly VsphereOptions Options;
        public readonly IConnectionService Connections = Substitute.For<IConnectionService>();
        public readonly Dictionary<string, VsphereConnection> Members = new();
        public readonly ConcurrentQueue<(Uri Uri, HttpMethod Method, byte[] Body)> Requests = new();
        public readonly byte[] Bytes = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        public readonly string StagedFile = Path.GetTempFileName();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        public readonly VsphereService Service;
        private readonly Handler _handler;

        public Storage(params VsphereHost[] hosts)
        {
            Options = new VsphereOptions { Hosts = hosts, IsoUploadViaApi = true };
            File.WriteAllBytes(StagedFile, Bytes);
            foreach (var host in hosts.Where(h => !string.IsNullOrWhiteSpace(h.Address)))
            {
                var client = Substitute.For<IVimClient>();
                var ds = new ManagedObjectReference { type = "Datastore", Value = "ds-1" };
                client.RetrievePropertiesAsync(
                    Arg.Any<ManagedObjectReference>(), Arg.Any<PropertyFilterSpec[]>())
                    .Returns(new RetrievePropertiesResponse(
                    [
                        new ObjectContent
                        {
                            obj = new ManagedObjectReference { type = "Datacenter", Value = "dc-1" },
                            propSet =
                            [
                                new DynamicProperty { name = "name", val = "datacenter" },
                                new DynamicProperty { name = "datastore", val = new[] { ds } }
                            ]
                        },
                        new ObjectContent
                        {
                            obj = ds,
                            propSet = [new DynamicProperty { name = "name", val = host.DsName }]
                        }
                    ]));
                var connection = new VsphereConnection(host, Options, NullLogger.Instance)
                {
                    Client = client,
                    Sic = new ServiceContent
                    {
                        rootFolder = new ManagedObjectReference { type = "Folder", Value = "root" },
                        fileManager = new ManagedObjectReference { type = "FileManager", Value = "files" }
                    }
                };
                typeof(VsphereConnection).GetProperty(nameof(VsphereConnection.Connected))
                    .SetValue(connection, true);
                Members[host.Address] = connection;
                Connections.GetConnection(host.Address).Returns(connection);
            }

            // Deliberately different from configuration order.
            Connections.GetAllConnections().Returns(Members.Values.Reverse());
            _handler = new Handler(async (request, ct) =>
            {
                var body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(ct);
                Requests.Enqueue((request.RequestUri, request.Method, body));
                return await Respond(request, ct);
            });
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient("vSphereDatastore").Returns(_ => new HttpClient(_handler, false));
            Service = new VsphereService(
                Microsoft.Extensions.Options.Options.Create(new RewriteHostOptions()),
                NullLogger<VsphereService>.Instance, Options, Substitute.For<IConfiguration>(),
                Connections, Substitute.For<IMapper>(), factory);
        }

        public void Disconnect(string address) =>
            typeof(VsphereConnection).GetProperty(nameof(VsphereConnection.Connected))
                .SetValue(Members[address], false);

        public Task<IsoOperationOutcome> Write(bool upload = true, CancellationToken ct = default) =>
            upload
                ? Service.UploadIso("view", "scope", "test.iso", StagedFile, ct)
                : Service.DeleteIso("view", "scope", "test.iso", ct);

        public void Dispose()
        {
            _handler.Dispose();
            File.Delete(StagedFile);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request, ct);
    }

    // Response disposal happens after the HTTP operation has finished successfully.
    private sealed class CancelOnDisposeContent(CancellationTokenSource cancellation) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                cancellation.Cancel();
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Default_WritesToEachConfiguredDestination(bool upload)
    {
        using var storage = new Storage(Host("first.test"), Host("second.test"));
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(2, storage.Requests.Count);
        Assert.All(storage.Requests, r => Assert.Equal(upload ? HttpMethod.Put : HttpMethod.Delete, r.Method));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shared_WritesOnceUsingConfigurationOrder_WithTwelveMembers(bool upload)
    {
        using var storage = new Storage(Enumerable.Range(0, 12).Select(i => Host($"vc{i}.test")).ToArray());
        storage.Options.IsoStorageShared = true;
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        var request = Assert.Single(storage.Requests);
        Assert.Equal("vc0.test", request.Uri.Host);
        Assert.Contains("folder-vc0.test/view/scope/test.iso", request.Uri.AbsolutePath);
        Assert.Contains("dsName=datastore-vc0.test", request.Uri.Query);
        Assert.Equal(12, storage.Service.GetEnabledConnectionCount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitGroups_OverrideEitherGlobalDefault(bool shared)
    {
        using var storage = new Storage(
            Host("a.test", "east"), Host("b.test", "east"),
            Host("c.test", "west"), Host("d.test"), Host("e.test"));
        storage.Options.IsoStorageShared = shared;
        var result = await storage.Write(ct: TestContext.Current.CancellationToken);
        Assert.Equal(shared ? 3 : 4, result.TotalHostCount);
        Assert.Equal(result.TotalHostCount, storage.Requests.Count);
        Assert.DoesNotContain(storage.Requests, r => r.Uri.Host == "b.test");
    }

    [Fact]
    public async Task NamesAreTrimmedAndCaseSensitive_AndCannotCollideWithIndividualDestinations()
    {
        using var storage = new Storage(
            Host("a.test", " group "), Host("b.test", "group"),
            Host("c.test", "Group"), Host("d.test", "e.test"),
            Host("e.test"), Host("f.test", " "));
        var result = await storage.Write(ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, result.TotalHostCount);
        Assert.Equal(5, storage.Requests.Count);
        Assert.DoesNotContain(storage.Requests, r => r.Uri.Host == "b.test");
    }

    [Fact]
    public async Task DisabledAndBlankAddressHosts_AreNotDestinations()
    {
        var disabled = Host("disabled.test", "unavailable");
        disabled.Enabled = false;
        using var storage = new Storage(disabled, Host(""), Host("active.test"));
        var result = await storage.Write(ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, result.TotalHostCount);
        Assert.Equal("active.test", Assert.Single(storage.Requests).Uri.Host);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void SharingConfiguration_AllowsMissingOrBlankValues(string value, bool shared)
    {
        var values = new Dictionary<string, string>();
        if (value != null)
            values["Vsphere:IsoStorageShared"] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new VsphereOptions();
        configuration.GetSection("Vsphere").Bind(options);
        Assert.Equal(shared, options.IsoStorageShared == true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectedMissingAndClientlessMembers_AreSkipped(bool upload)
    {
        using var storage = new Storage(
            Host("offline.test"), Host("missing.test"), Host("clientless.test"), Host("online.test"));
        storage.Options.IsoStorageShared = true;
        storage.Disconnect("offline.test");
        storage.Connections.GetConnection("missing.test").Returns((VsphereConnection)null);
        storage.Members["clientless.test"].Client = null;
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal("online.test", Assert.Single(storage.Requests).Uri.Host);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.BadGateway)]
    [InlineData(false, HttpStatusCode.BadGateway)]
    [InlineData(true, HttpStatusCode.GatewayTimeout)]
    [InlineData(false, HttpStatusCode.GatewayTimeout)]
    public async Task FailedAttempt_FallsBackAndStopsAfterSuccess(bool upload, HttpStatusCode failureStatus)
    {
        using var storage = new Storage(Host("a.test"), Host("b.test"), Host("c.test"));
        storage.Options.IsoStorageShared = true;
        storage.Respond = (r, _) => Task.FromResult(new HttpResponseMessage(
            r.RequestUri.Host == "a.test" ? failureStatus : HttpStatusCode.OK));
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(new[] { "a.test", "b.test" }, storage.Requests.Select(r => r.Uri.Host));
        if (upload)
            Assert.All(storage.Requests, r => Assert.Equal(storage.Bytes, r.Body));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttemptTimeout_PermitsFallback(bool upload)
    {
        using var storage = new Storage(Host("a.test", "shared"), Host("b.test", "shared"));
        storage.Respond = (r, _) => r.RequestUri.Host == "a.test"
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("attempt timed out"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(new[] { "a.test", "b.test" }, storage.Requests.Select(r => r.Uri.Host));
        if (upload)
            Assert.All(storage.Requests, r => Assert.Equal(storage.Bytes, r.Body));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EntirelyOfflineGroup_IsCountedAlongsideSuccessfulGroups(bool upload)
    {
        using var storage = new Storage(
            Host("offline-a.test", "offline"), Host("offline-b.test", "offline"), Host("online.test"));
        storage.Disconnect("offline-a.test");
        storage.Disconnect("offline-b.test");
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.TotalHostCount);
        Assert.Equal(1, result.FailedHostCount);
        Assert.Single(storage.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AllGroupsFail_ReturnsKnownCountsForProviderAggregation(bool upload)
    {
        using var storage = new Storage(Host("offline.test"), Host("fails.test", "broken"));
        storage.Disconnect("offline.test");
        storage.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.TotalHostCount);
        Assert.Equal(2, result.FailedHostCount);
    }

    [Fact]
    public async Task NoConfiguredHosts_IsAnError()
    {
        using var storage = new Storage();
        storage.Options.Hosts = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Write(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingDelete_IsSuccessWithoutFallback()
    {
        using var storage = new Storage(Host("a.test", "shared"), Host("b.test", "shared"));
        storage.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await storage.Write(upload: false, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Single(storage.Requests);
    }

    [Fact]
    public async Task GroupsRunConcurrently_ButMembersWithinAGroupRunSequentially()
    {
        using var storage = new Storage(Host("a.test", "one"), Host("b.test", "one"), Host("c.test", "two"));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.Respond = async (r, ct) =>
        {
            if (r.RequestUri.Host == "a.test")
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (r.RequestUri.Host == "c.test")
                otherStarted.SetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var operation = storage.Write(ct: timeout.Token);
        try
        {
            await Task.WhenAll(firstStarted.Task, otherStarted.Task).WaitAsync(timeout.Token);
            Assert.DoesNotContain(storage.Requests, r => r.Uri.Host == "b.test");
        }
        finally
        {
            releaseFirst.TrySetResult();
            await operation;
        }
        Assert.Equal(0, (await operation).FailedHostCount);
        Assert.Equal(3, storage.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellation_StopsAttempts(bool upload)
    {
        using var storage = new Storage(Host("a.test", "shared"), Host("b.test", "shared"));
        using var cancellation = new CancellationTokenSource();
        storage.Respond = (_, ct) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(ct);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.Write(upload, cancellation.Token));
        Assert.Single(storage.Requests);
    }

    [Fact]
    public async Task AlreadyCancelledRequest_PerformsNoWrites()
    {
        using var storage = new Storage(Host("a.test"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.Write(ct: new CancellationToken(true)));
        Assert.Empty(storage.Requests);
    }

    [Fact]
    public async Task CancellationAfterInventoryLookup_DoesNotCreateDirectoryOrTryAnotherMember()
    {
        using var storage = new Storage(Host("a.test", "shared"), Host("b.test", "shared"));
        using var cancellation = new CancellationTokenSource();
        var client = storage.Members["a.test"].Client;
        var inventory = await client.RetrievePropertiesAsync(null, Array.Empty<PropertyFilterSpec>());
        client.RetrievePropertiesAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<PropertyFilterSpec[]>())
            .Returns(_ =>
            {
                // A completed lookup can be returned even though cancellation has just arrived.
                // The next SOAP operation must check before starting directory creation.
                cancellation.Cancel();
                return inventory;
            });
        storage.Connections.ClearReceivedCalls();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.Write(ct: cancellation.Token));

        await client.DidNotReceive().MakeDirectoryAsync(
            Arg.Any<ManagedObjectReference>(), Arg.Any<string>(), Arg.Any<ManagedObjectReference>(), Arg.Any<bool>());
        storage.Connections.DidNotReceive().GetConnection("b.test");
        Assert.Empty(storage.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAfterSuccessfulOperation_PreservesItsOutcome(bool upload)
    {
        using var storage = new Storage(Host("a.test", "shared"), Host("b.test", "shared"));
        using var cancellation = new CancellationTokenSource();
        storage.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new CancelOnDisposeContent(cancellation)
        });

        var outcome = await storage.Write(upload, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, outcome.TotalHostCount);
        Assert.Equal(0, outcome.FailedHostCount);
        Assert.Single(storage.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationRacingAnUnrelatedError_StopsAttemptsAsCancellation(bool upload)
    {
        using var storage = new Storage(Host("a.test", "shared"), Host("b.test", "shared"));
        using var cancellation = new CancellationTokenSource();
        storage.Respond = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromException<HttpResponseMessage>(new InvalidOperationException("member failed"));
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.Write(upload, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(storage.Requests);
    }
}
