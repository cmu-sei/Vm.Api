// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Vsphere.Options;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// One Proxmox cluster holding whatever machines a test registers, substituted at the socket rather
/// than at an interface: <see cref="ProxmoxService"/> builds its own <c>PveClient</c> in its
/// constructor from <c>IHttpClientFactory.CreateClient("proxmox")</c>, so replacing the transport
/// leaves the whole client - its route building, its <c>{"data": ...}</c> envelope, its typed model
/// binding and its task waiting - running for real.
/// </summary>
/// <remarks>
/// <para>
/// That is the point. A substitute at <c>PveClient</c> would assert only that the test and the service
/// agree about a method name; here a test asserts the request Proxmox would actually have received,
/// which is what the 907 lines of this driver exist to produce. It also means the arrangements below
/// are written in Proxmox's vocabulary - paths and JSON - not in the client's.
/// </para>
/// <para>
/// Two costs are worth knowing before writing a test against this. <see cref="Accepts"/> answers
/// <c>{"data":null}</c>, which PveClient's <c>WaitAndThrow</c> reads as "nothing to wait for" and
/// returns from immediately; that is the cheap path and what most arrangements want. A submit that
/// answers a real <c>UPID:</c> string - <see cref="SubmitsTask"/> - makes the client sleep two seconds
/// before its first poll, with no interval setting to turn that down, so use it only where the waiting
/// itself is the subject. <see cref="Options"/> likewise sets <c>GuestProcessPollMs</c> to zero,
/// because at the production default of 500ms every guest-process test would pay it per poll.
/// </para>
/// </remarks>
public sealed class FakeProxmoxCluster
{
    public const string Host = "pve.example.test";
    public const string DefaultNode = "pve1";

    /// <summary>
    /// Shaped like a real one - <c>user@realm!tokenid=secret</c> - because the header it produces is
    /// the raw string, so a test asserting authorization is asserting the shape a deployment uses.
    /// </summary>
    public const string ApiToken = "player@pve!vmapi=1c1e0f2a-0000-4000-8000-000000000000";

    public const string ClusterResources = "api2/json/cluster/resources";
    public const string ClusterTasks = "api2/json/cluster/tasks";

    public readonly TestHttpHandler Http = new();

    public readonly IProxmoxStateService State = Substitute.For<IProxmoxStateService>();

    // GuestProcessPollMs = 0 so the exec-status loop spins without a real delay; see the class remarks.
    public readonly ProxmoxOptions Options = new()
    {
        Enabled = true,
        Host = Host,
        Port = 8006,
        Token = ApiToken,
        GuestProcessPollMs = 0,
    };

    /// <summary>
    /// Off by default, which is the deployment that hands the client the Proxmox host directly. Set it
    /// to exercise the reverse-proxied console URL instead.
    /// </summary>
    public RewriteHostOptions RewriteHost { get; set; } = new();

    private readonly List<Machine> _machines = [];

    public FakeProxmoxCluster()
    {
        // Registered once and computed per request, because a test adds machines after construction and
        // Migrates() changes what this same path reports on its next read.
        Http.AnswersJson($"GET {ClusterResources}", () => Data(
            "[" + string.Join(',', _machines.Select(x => x.Json())) + "]"));
    }

    /// <summary>
    /// The service under test, built fresh so a test can arrange first and construct after.
    /// </summary>
    /// <remarks>
    /// The context is null by default, and that is an assertion rather than a shortcut: only
    /// <c>GetCurrentNodeForVm</c> and <c>BulkPowerOperation</c> read the database, so every other
    /// method throwing a <see cref="NullReferenceException"/> here would mean one of them had started
    /// to. Those two pass a real context from <see cref="DatabaseTestBase"/>.
    /// </remarks>
    public ProxmoxService Service(VmContext dbContext = null) =>
        new(Options,
            NullLogger<ProxmoxService>.Instance,
            State,
            RewriteHost,
            dbContext,
            Factory());

    /// <summary>
    /// A machine the cluster knows about, and the <see cref="ProxmoxVmInfo"/> a caller would hold for
    /// it. Registering none is how a test spells a vmid Proxmox has never heard of.
    /// </summary>
    /// <param name="status">
    /// As <c>/cluster/resources</c> reports it: <c>running</c>, <c>stopped</c>, <c>paused</c>, or
    /// <c>unknown</c>. Anything else leaves every power flag false, which reads as
    /// <c>PowerState.Unknown</c>.
    /// </param>
    public ProxmoxVmInfo Has(
        int id,
        ProxmoxVmType type = ProxmoxVmType.QEMU,
        string status = "running",
        string node = DefaultNode,
        Guid vmId = default)
    {
        _machines.Add(new Machine(id, node, type, status));

        return new ProxmoxVmInfo
        {
            VmId = vmId,
            Id = id,
            Node = node,
            Type = type,
        };
    }

    /// <summary>
    /// Moves a machine to another node without telling the caller's <see cref="ProxmoxVmInfo"/>, which
    /// is exactly the state a migration leaves behind: the stored node is only refreshed by the state
    /// poller, so until it runs every <c>Nodes[Node]</c> call is addressed to the node the machine has
    /// left.
    /// </summary>
    public void Migrates(int id, string toNode)
    {
        var index = _machines.FindIndex(x => x.Id == id);

        _machines[index] = _machines[index] with { Node = toNode };
    }

    /// <summary>
    /// A mutating call Proxmox takes without queueing a task. The cheap happy path - see the class
    /// remarks for why <c>{"data":null}</c> and not a UPID.
    /// </summary>
    public FakeProxmoxCluster Accepts(string pattern)
    {
        Http.AnswersJson(pattern, Data("null"));

        return this;
    }

    /// <summary>
    /// A mutating call that queues a real Proxmox task, whose status the client then polls to
    /// completion. Costs two seconds of wall clock; only for tests whose subject is the wait.
    /// </summary>
    /// <param name="exitStatus">
    /// <c>OK</c> for a task that succeeded. Anything else is how a task that ran and failed reports
    /// itself, which <c>WaitAndThrow</c> turns into an exception.
    /// </param>
    public FakeProxmoxCluster SubmitsTask(
        string pattern, string exitStatus = "OK", string node = DefaultNode)
    {
        var upid = $"UPID:{node}:0000ABCD:0011:0022:qmstart:100:{ApiToken}:";

        Http.AnswersJson(pattern, Data($"\"{upid}\""));
        Http.AnswersJson(
            $"GET api2/json/nodes/{node}/tasks/{upid}/status",
            Data($"{{\"status\":\"stopped\",\"exitstatus\":\"{Escape(exitStatus)}\"}}"));

        return this;
    }

    /// <summary>
    /// A refusal carrying a Proxmox error. The <c>errors</c> object is the load-bearing part: a bare
    /// 500 leaves <c>Result.GetError()</c> empty, so a test asserting on a message has to supply one.
    /// </summary>
    public FakeProxmoxCluster Rejects(
        string pattern,
        string message,
        string field = "",
        HttpStatusCode status = HttpStatusCode.InternalServerError,
        bool once = false)
    {
        Http.AnswersJson(
            pattern,
            $"{{\"data\":null,\"errors\":{{\"{Escape(field)}\":\"{Escape(message)}\"}}}}",
            status,
            once);

        return this;
    }

    /// <summary>Answers a path with a <c>data</c> payload written as Proxmox would send it.</summary>
    public FakeProxmoxCluster Answers(string pattern, string dataJson)
    {
        Http.AnswersJson(pattern, Data(dataJson));

        return this;
    }

    /// <summary>The route a call about one machine is addressed to, as a rule spells it.</summary>
    public static string VmPath(ProxmoxVmInfo info, string tail = "") =>
        VmPath(info.Node, info.Type, info.Id, tail);

    public static string VmPath(string node, ProxmoxVmType type, int id, string tail = "") =>
        $"api2/json/nodes/{node}/{(type == ProxmoxVmType.LXC ? "lxc" : "qemu")}/{id}{tail}";

    public static string NodePath(string node, string tail) =>
        $"api2/json/nodes/{node}{tail}";

    /// <summary>The one request that went to a method and path, for asserting on what it carried.</summary>
    public TestHttpHandler.SentRequest Request(HttpMethod method, string path) =>
        Http.Sent.Single(x => x.Method == method && x.Path == path);

    /// <summary>Every request to a method and path, for counting retries and cache misses.</summary>
    public IReadOnlyList<TestHttpHandler.SentRequest> Requests(HttpMethod method, string path) =>
        Http.Sent.Where(x => x.Method == method && x.Path == path).ToList();

    /// <summary>The <c>{"data": ...}</c> envelope every Proxmox response arrives in.</summary>
    public static string Data(string json) => $"{{\"data\":{json}}}";

    private IHttpClientFactory Factory()
    {
        var factory = Substitute.For<IHttpClientFactory>();

        // A fresh client per call over the one handler, deliberately not matched on the client name:
        // ProxmoxIsoStorageService's upload path sets Timeout on what it is handed, which throws on a
        // client that has already sent a request, so a cached instance would fail every upload but the
        // first. disposeHandler: false keeps Sent readable after the client is disposed.
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(Http, disposeHandler: false));

        return factory;
    }

    private static string Escape(string value) =>
        value?.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>One row of <c>/cluster/resources?type=vm</c>.</summary>
    private sealed record Machine(int Id, string Node, ProxmoxVmType Type, string Status)
    {
        public string Json() =>
            $"{{\"vmid\":{Id.ToString(CultureInfo.InvariantCulture)}," +
            $"\"node\":\"{Node}\"," +
            $"\"type\":\"{(Type == ProxmoxVmType.LXC ? "lxc" : "qemu")}\"," +
            $"\"status\":\"{Status}\"}}";
    }
}
