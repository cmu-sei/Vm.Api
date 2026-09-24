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
using Player.Vm.Api.Features.Files.Providers;
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
            Options = new VsphereOptions { Hosts = hosts, IsoUploadViaApi = true, TaskPollIntervalMilliseconds = 0 };
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
                    Connected = true,
                    Sic = new ServiceContent
                    {
                        rootFolder = new ManagedObjectReference { type = "Folder", Value = "root" },
                        fileManager = new ManagedObjectReference { type = "FileManager", Value = "files" }
                    }
                };
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
            Members[address].Connected = false;

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Default_WritesToEachConfiguredDestination(bool upload)
    {
        using var storage = new Storage(Host("first.test"), Host("second.test"));
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(new[] { "first.test", "second.test" }, storage.Requests.Select(r => r.Uri.Host).Order());
        Assert.All(storage.Requests, r => Assert.Equal(upload ? HttpMethod.Put : HttpMethod.Delete, r.Method));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shared_WritesOnceUsingConfigurationOrder(bool upload)
    {
        using var storage = new Storage(Enumerable.Range(0, 3).Select(i => Host($"vc{i}.test")).ToArray());
        storage.Options.IsoStorageShared = true;
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        var request = Assert.Single(storage.Requests);
        Assert.Equal("vc0.test", request.Uri.Host);
        Assert.Contains("folder-vc0.test/view/scope/test.iso", request.Uri.AbsolutePath);
        Assert.Contains("dsName=datastore-vc0.test", request.Uri.Query);
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
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(shared ? new[] { "a.test", "c.test", "d.test" } : new[] { "a.test", "c.test", "d.test", "e.test" },
            storage.Requests.Select(r => r.Uri.Host).Order());
    }

    [Fact]
    public async Task GroupNames_AreTrimmed()
    {
        using var storage = new Storage(Host("a.test", " group "), Host("b.test", "group"));

        var result = await storage.Write(ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal("a.test", Assert.Single(storage.Requests).Uri.Host);
    }

    [Fact]
    public async Task GroupNames_AreCaseSensitive()
    {
        using var storage = new Storage(Host("a.test", "group"), Host("b.test", "Group"));

        var result = await storage.Write(ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(new[] { "a.test", "b.test" }, storage.Requests.Select(r => r.Uri.Host).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlankGroupNames_AreUnassigned(bool shared)
    {
        using var storage = new Storage(Host("a.test", " "), Host("b.test"));
        storage.Options.IsoStorageShared = shared;

        var result = await storage.Write(ct: TestContext.Current.CancellationToken);

        Assert.Equal(shared ? 1 : 2, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(shared ? new[] { "a.test" } : new[] { "a.test", "b.test" },
            storage.Requests.Select(r => r.Uri.Host).Order());
    }

    [Fact]
    public async Task GroupNameMatchingAHostAddress_RemainsASeparateDestination()
    {
        using var storage = new Storage(Host("a.test", "b.test"), Host("b.test"));

        var result = await storage.Write(ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(new[] { "a.test", "b.test" }, storage.Requests.Select(r => r.Uri.Host).Order());
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectedMember_IsSkipped(bool upload)
    {
        using var storage = new Storage(Host("offline.test"), Host("online.test"));
        storage.Options.IsoStorageShared = true;
        storage.Disconnect("offline.test");
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.TotalHostCount);
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
        // An upload repeats a 5xx PUT on the same member once before falling back.
        Assert.Equal(upload ? new[] { "a.test", "a.test", "b.test" } : new[] { "a.test", "b.test" },
            storage.Requests.Select(r => r.Uri.Host));
        var retry = storage.Requests.Last();
        Assert.Equal("/folder/folder-b.test/view/scope/test.iso", retry.Uri.AbsolutePath);
        Assert.Contains("dsName=datastore-b.test", retry.Uri.Query);
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
    public async Task AllGroupsFail_ReturnsFailureCounts(bool upload)
    {
        using var storage = new Storage(Host("offline.test"), Host("fails.test", "broken"));
        storage.Disconnect("offline.test");
        storage.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var result = await storage.Write(upload, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.TotalHostCount);
        Assert.Equal(2, result.FailedHostCount);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Groups_CanBothStartBeforeEitherCompletes(bool upload)
    {
        using var storage = new Storage(Host("a.test", "one"), Host("b.test", "two"));
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        storage.Respond = async (_, ct) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2)
                bothStarted.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var timeout = TimeSpan.FromSeconds(10);
        var ct = TestContext.Current.CancellationToken;
        var operation = storage.Write(upload, ct);
        IsoOperationOutcome result;
        try
        {
            await bothStarted.Task.WaitAsync(timeout, ct);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            result = await operation.WaitAsync(timeout, ct);
        }
        Assert.Equal(2, result.TotalHostCount);
        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(new[] { "a.test", "b.test" }, storage.Requests.Select(r => r.Uri.Host).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_MultipleScopes_CountsEachStorageGroupPerScope(bool firstScopeFails)
    {
        using var storage = new Storage(
            Host("a.test", "east"), Host("b.test", "east"), Host("c.test", "west"));
        var viewId = Guid.NewGuid();
        var scopes = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        storage.Respond = (r, _) => Task.FromResult(new HttpResponseMessage(
            r.RequestUri.Host == "a.test" ||
            (firstScopeFails && r.RequestUri.AbsolutePath.Contains($"/{scopes[0]}/"))
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK));
        var provider = new VsphereIsoProvider(storage.Service, storage.Options);

        var result = await provider.UploadAsync(
            new IsoUploadRequest(viewId, scopes, "test.iso", storage.StagedFile, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, result.TotalHostCount);
        Assert.Equal(firstScopeFails ? 2 : 0, result.FailedHostCount);
        var requests = storage.Requests.ToArray();
        Assert.Equal(firstScopeFails ? 10 : 8, requests.Length);
        foreach (var scope in scopes)
        {
            foreach (var host in new[] { "a.test", "b.test", "c.test" })
            {
                // Every failing PUT is a 503, so each is repeated once on the same member.
                var fails = host == "a.test" || (firstScopeFails && scope == scopes[0]);
                var matching = requests.Where(r => r.Uri.Host == host &&
                    r.Uri.AbsolutePath == $"/folder/folder-{host}/{viewId}/{scope}/test.iso").ToArray();
                Assert.Equal(fails ? 2 : 1, matching.Length);
                Assert.All(matching, request =>
                {
                    Assert.Contains($"dsName=datastore-{host}", request.Uri.Query);
                    Assert.Equal(HttpMethod.Put, request.Method);
                    Assert.Equal(storage.Bytes, request.Body);
                });
            }
        }
    }

    [Fact]
    public async Task Upload_TransientServerError_IsRetriedOnTheSameMember()
    {
        using var storage = new Storage(Host("a.test"));
        var attempts = 0;
        storage.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(
            Interlocked.Increment(ref attempts) == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.Created));

        var result = await storage.Write(ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.FailedHostCount);
        Assert.Equal(2, storage.Requests.Count);
        Assert.All(storage.Requests, r => Assert.Equal(storage.Bytes, r.Body));
    }

    [Fact]
    public async Task Upload_ClientError_IsNotRetried()
    {
        using var storage = new Storage(Host("a.test"));
        storage.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await storage.Write(ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.FailedHostCount);
        Assert.Single(storage.Requests);
    }

    [Fact]
    public async Task Delete_ServerError_IsNotRetriedOnTheSameMember()
    {
        using var storage = new Storage(Host("a.test"));
        storage.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await storage.Write(upload: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.FailedHostCount);
        Assert.Single(storage.Requests);
    }

    // Every scope folder is created, one at a time, on the first member of each group that manages it.
    [Fact]
    public async Task PrepareIsoFolders_CreatesEachScopeFolderOncePerGroup()
    {
        using var storage = new Storage(Host("a.test", "east"), Host("b.test", "east"), Host("c.test"));
        var scopes = new[] { "scope-1", "scope-2", "scope-3" };

        await storage.Service.PrepareIsoFolders("view", scopes, TestContext.Current.CancellationToken);

        foreach (var host in new[] { "a.test", "c.test" })
        {
            var created = storage.Members[host].Client.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(IVimClient.MakeDirectoryAsync))
                .Select(c => (string)c.GetArguments()[1]);
            Assert.Equal(scopes.Select(scope => $"[datastore-{host}] folder-{host}/view/{scope}"), created);
        }
        await storage.Members["b.test"].Client.DidNotReceiveWithAnyArgs()
            .MakeDirectoryAsync(default, default, default, default);
        Assert.Empty(storage.Requests);
    }

    [Fact]
    public async Task PrepareIsoFolders_FailedMember_FallsBackWithoutThrowing()
    {
        using var storage = new Storage(Host("a.test", "shared"), Host("b.test", "shared"));
        storage.Members["a.test"].Client
            .MakeDirectoryAsync(Arg.Any<ManagedObjectReference>(), Arg.Any<string>(), Arg.Any<ManagedObjectReference>(), Arg.Any<bool>())
            .Returns(Task.FromException(new InvalidOperationException("mkdir failed")));

        await storage.Service.PrepareIsoFolders("view", ["scope"], TestContext.Current.CancellationToken);

        await storage.Members["b.test"].Client.Received(1).MakeDirectoryAsync(
            Arg.Any<ManagedObjectReference>(), "[datastore-b.test] folder-b.test/view/scope",
            Arg.Any<ManagedObjectReference>(), true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellation_InterruptsTheRequestWithoutFallback(bool upload)
    {
        using var storage = new Storage(Host("a.test", "shared"), Host("b.test", "shared"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.Respond = async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var timeout = TimeSpan.FromSeconds(10);
        var ct = TestContext.Current.CancellationToken;
        var operation = storage.Write(upload, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(timeout, ct);
            cancellation.Cancel();

            // The wait must not use the caller token: it would hide a request that ignored cancellation.
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => operation.WaitAsync(timeout, ct));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal("a.test", Assert.Single(storage.Requests).Uri.Host);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try
            {
                await operation.WaitAsync(timeout, ct);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Observe the cancelled operation; release also lets a broken, uncancellable request finish.
            }
        }
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
