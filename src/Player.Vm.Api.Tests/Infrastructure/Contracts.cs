// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// The checked-in files under <c>contracts/</c>, and the one place that knows where they are.
/// </summary>
/// <remarks>
/// <para>
/// Both files are generated. Neither is authored: <c>ContractTests</c> regenerates
/// <c>signalr-contract.json</c> from the mapped hubs and the real broadcast producers, and
/// <c>OpenApiSurfaceTests</c> regenerates <c>openapi-surface.json</c> from the document the hosted
/// application serves. What they are for is that something outside this repository reads the same
/// bytes - <c>crucible-tests</c> checks the Angular clients against both - which nothing in a build
/// output or a test fixture could give it.
/// </para>
/// <para>
/// Read from the repository rather than from a copy in the test output, because a regeneration has to
/// land where git can see it. The path comes from the test project's own <c>AssemblyMetadata</c>,
/// which is the only thing that knows it at build time.
/// </para>
/// <para>
/// A missing directory throws rather than skipping. A contract test that quietly passes when it
/// cannot find its contract is worth less than no test: this suite has one because nothing else in
/// the estate compares the two sides at all.
/// </para>
/// </remarks>
internal static class Contracts
{
    /// <summary>
    /// Set to <c>1</c> to make the contract tests rewrite the files they generate instead of asserting
    /// against them. Deliberately an environment variable and not a constant: regenerating is something
    /// a person does after reading a diff, never something a run decides for itself.
    /// </summary>
    public const string UpdateVariable = "VMAPI_UPDATE_CONTRACTS";

    public const string SignalRFileName = "signalr-contract.json";
    public const string OpenApiSurfaceFileName = "openapi-surface.json";

    /// <summary>
    /// The options the contract files are written and read with. Indented and with the properties in
    /// the order the records declare them, because these files are read by people in a diff.
    /// </summary>
    public static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,

        // So a media type comes out as "application/*+json" rather than with the plus escaped to
        // +. Nothing here is written into HTML, and these files are read in a diff.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Directory { get; } = Locate();

    public static string PathTo(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>The SignalR contract, parsed. Read once: the file does not change during a run.</summary>
    public static SignalRContract SignalR { get; } = Read();

    /// <summary>The contract file, as a mutable document to regenerate the derivable parts of.</summary>
    public static async Task<JsonObject> ReadDocument(string fileName, CancellationToken ct) =>
        JsonNode.Parse(await File.ReadAllTextAsync(PathTo(fileName), ct)).AsObject();

    /// <summary>
    /// The bytes a contract file is written as: indented JSON with a trailing newline, so a regenerated
    /// file is what an editor and git both expect and a diff is one line per fact that moved.
    /// </summary>
    public static string Render(JsonNode content) =>
        JsonSerializer.Serialize(content, FileOptions) + "\n";

    /// <summary>
    /// Line endings only. These files are committed to a repository that is cloned on Windows as well,
    /// and a checkout that translated them would fail a generated file against itself.
    /// </summary>
    public static string Normalize(string text) => text.ReplaceLineEndings("\n");

    /// <summary>
    /// The regenerate-or-assert protocol both generated files use: with <see cref="UpdateVariable"/>
    /// set, rewrite the file and fail; otherwise assert the committed bytes are the generated ones.
    /// </summary>
    /// <remarks>
    /// Shared so that neither file can drift from the other's handling of it, and - the part that
    /// matters - so neither test can be the one that forgets the deliberate failure below.
    /// </remarks>
    public static async Task AssertMatchesOrRewrite(
        string fileName, string regenerated, CancellationToken ct)
    {
        var path = PathTo(fileName);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, regenerated, ct);

            // Deliberately a failure. Regenerating is not a way of passing: a run with the variable set
            // must never be green, or a pipeline that inherited it would rewrite the file on every
            // build and the test would never say anything again.
            Assert.Fail(
                $"Rewrote '{path}' because {UpdateVariable}=1. Read the diff, then re-run without the " +
                "variable.");
        }

        Assert.Equal(
            Normalize(await File.ReadAllTextAsync(path, ct)),
            Normalize(regenerated));
    }

    private static string Locate()
    {
        var configured = typeof(Contracts).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(x => x.Key == "ContractsDirectory")?.Value
            ?? throw new InvalidOperationException(
                "The test assembly has no ContractsDirectory metadata. It is set in " +
                "Player.Vm.Api.Tests.csproj; a build that dropped it leaves the contract tests with " +
                "nothing to compare against.");

        var directory = Path.GetFullPath(configured);

        if (!System.IO.Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The contracts directory '{directory}' does not exist. It is checked in at the root " +
                "of this repository.");
        }

        return directory;
    }

    private static SignalRContract Read()
    {
        var path = PathTo(SignalRFileName);

        return JsonSerializer.Deserialize<SignalRContract>(File.ReadAllText(path), FileOptions)
            ?? throw new InvalidOperationException($"'{path}' parsed as null.");
    }
}

/// <summary>The shape of <c>contracts/signalr-contract.json</c>.</summary>
internal sealed record SignalRContract(
    string Description,
    IReadOnlyList<ContractHub> Hubs,
    ContractModifiedProperties ModifiedProperties)
{
    /// <summary>The hub with this name, or a failure naming the file rather than a null reference.</summary>
    public ContractHub Hub(string name) =>
        Hubs.SingleOrDefault(x => x.Name == name)
        ?? throw new InvalidOperationException(
            $"'{Contracts.SignalRFileName}' declares no hub named '{name}'.");
}

internal sealed record ContractHub(
    string Name,
    string Path,
    string HubType,
    IReadOnlyList<ContractClient> Clients,
    IReadOnlyList<ContractInvocation> Invocations,
    IReadOnlyList<ContractBroadcast> Broadcasts,
    IReadOnlyList<ContractUnsentListener> ClientListenersWithNoSender);

internal sealed record ContractClient(string App, string Source);

internal sealed record ContractInvocation(string Name, int Arguments, ContractReturn Returns, string Note);

internal sealed record ContractReturn(bool Collection, IReadOnlyList<string> Keys);

internal sealed record ContractBroadcast(
    string Name,
    IReadOnlyList<int> Arguments,
    IReadOnlyList<string> SentBy,
    string Note);

internal sealed record ContractUnsentListener(
    string Name,
    IReadOnlyList<string> ListenedForBy,
    string Note);

internal sealed record ContractModifiedProperties(
    string Description,
    IReadOnlyList<string> Names,
    ContractNeverSent NeverSent);

internal sealed record ContractNeverSent(string Description, IReadOnlyList<string> Keys);
