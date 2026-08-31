// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Crucible.Common.EntityEvents.Events;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Features.Vms.EventHandlers;
using Player.Vm.Api.Features.Vms.Hubs;
using Player.Vm.Api.Infrastructure.Extensions;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;
using VmDto = Player.Vm.Api.Features.Vms.Vm;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The server half of <c>contracts/signalr-contract.json</c>: that the hubs the application maps, the
/// methods they declare, the messages they broadcast and the <c>modifiedProperties</c> names they send
/// are the ones the shared list says they are - and, in
/// <see cref="TheContract_IsWhatTheApplicationGenerates"/>, the generator that puts them there.
/// </summary>
/// <remarks>
/// <para>
/// The file is generated, not authored. Everything in it that this repository can be asked for is taken
/// from the application; what is carried forward is the prose and the facts about the browser clients,
/// which no amount of reflection here could produce. Regenerate it with
/// <c>VMAPI_UPDATE_CONTRACTS=1 dotnet test --filter FullyQualifiedName~ContractTests</c>.
/// </para>
/// <para>
/// Every string in that file is written twice in the estate, once here and once in <c>vm.ui</c> or
/// <c>console.ui</c>, and nothing compares the two copies. SignalR does not either. An invocation the
/// server does not declare fails that one call with a <c>HubException</c> and leaves the connection up;
/// a broadcast name only one side knows about arrives nowhere at all, and the Vm list in a browser
/// stays silently stale until someone reloads it. Neither shows up as an error anywhere an operator
/// would look.
/// </para>
/// <para>
/// This class asserts nothing about behaviour. What the hubs do with the names - which group each one
/// joins, who hears each broadcast - is <c>VmHubGroupTests</c>, <c>VmHubPresenceTests</c>,
/// <c>ProgressHubTests</c> and <c>VmSignalRHandlerTests</c>; that the endpoints exist and refuse an
/// anonymous caller is <c>HubConnectionTests</c>. What none of those can do is notice that the client
/// is listening for something else, because none of them has ever seen the client. This is the join,
/// and <c>crucible-tests/playerVm/tests/contract/signalr-contract.spec.ts</c> is the same join from the
/// other side.
/// </para>
/// <para>
/// The broadcast names and arities are taken by driving the real producers into a recording hub
/// context, not by reading the constants: a constant proves what the name is spelled as, and the point
/// here is what actually goes on the wire and how many arguments it has. The one exception is
/// <c>Progress</c>, which no constant covers either - see
/// <see cref="TheProgressBroadcast_IsTheLiteralBothPollersSend"/>.
/// </para>
/// </remarks>
public class ContractTests : ApiTestBase, IClassFixture<VmApiFactory>
{
    private readonly IViewService _views = Substitute.For<IViewService>();
    private readonly IPlayerService _player = Substitute.For<IPlayerService>();
    private readonly IVmService _vms = Substitute.For<IVmService>();
    private readonly IVmUsageLoggingService _usageLog = Substitute.For<IVmUsageLoggingService>();

    /// <summary>What the entity-event handlers broadcast, which they do through an injected hub context.</summary>
    private readonly HubContextHarness<VmHub> _fromHandlers = new();

    /// <summary>What the hub itself broadcasts, which it does through its own <c>Clients</c>.</summary>
    private readonly HubHarness _fromHub = new(Guid.NewGuid(), "alice");

    private readonly ServiceProvider _provider;
    private readonly IActiveVirtualMachineService _active;
    private readonly List<VmContext> _contexts = [];

    /// <inheritdoc cref="VmHubPresenceTests"/>
    /// <remarks>
    /// The container is here for the same reason it is in <c>VmHubPresenceTests</c>: the real
    /// <see cref="ActiveVirtualMachineService"/> resolves <see cref="IViewService"/> from a scope of its
    /// own rather than taking it as a dependency.
    /// </remarks>
    public ContractTests(DatabaseFixture fixture, VmApiFactory factory) : base(fixture, factory)
    {
        _views.GetInfoForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns([]);

        _provider = new ServiceCollection()
            .AddSingleton(_views)
            .BuildServiceProvider();

        _active = new ActiveVirtualMachineService(
            _provider.GetRequiredService<IServiceScopeFactory>(), new TelemetryService());
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        await _provider.DisposeAsync();
        await base.DisposeAsync();
    }

    #region The hubs

    /// <summary>
    /// The contract names the hubs the application maps, at the paths it maps them, and no others. A hub
    /// added and mapped without being added here would have no client checked against it at all, and a
    /// path moved without being changed here would leave the clients checked against the old one.
    /// </summary>
    /// <remarks>
    /// Both halves come from the endpoints rather than from literals, which is what makes the path in the
    /// file - the one <c>crucible-tests</c> matches the Angular clients' <c>withUrl</c> against - the
    /// application's own. <c>HubConnectionTests</c> has the two paths written out as constants and
    /// connects to them, which is a different question: that the endpoint is there and refuses an
    /// anonymous caller. Nothing but this compares a path to where <c>MapHub</c> actually put it.
    /// </remarks>
    [Fact]
    public void TheContract_NamesEveryHubTheApplicationMapsAtThePathItMapsIt()
    {
        Assert.Equal<string>(
            [.. Contracts.SignalR.Hubs.Select(x => $"{x.HubType} at {x.Path}").Order()],
            [.. MappedHubs().Select(x => $"{x.Key} at {x.Value}").Order()]);
    }

    /// <summary>
    /// The hubs the application maps, as the full name of each hub type to the path it is mapped at.
    /// </summary>
    /// <remarks>
    /// <c>MapHub</c> adds two endpoints per hub: the connection at the path, and a negotiate POST one
    /// segment below it. The negotiate endpoint is dropped because it belongs to the transport - a client
    /// is given the hub path and the protocol appends the rest - so it is not part of what the two sides
    /// agree on.
    /// </remarks>
    private Dictionary<string, string> MappedHubs() =>
        Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(x => x.Metadata.GetMetadata<HubMetadata>() is not null)
            .Where(x => !x.RoutePattern.RawText.EndsWith("/negotiate", StringComparison.Ordinal))
            .ToDictionary(
                x => x.Metadata.GetMetadata<HubMetadata>().HubType.FullName,
                x => x.RoutePattern.RawText);

    /// <summary>
    /// Every hub in the contract names at least one client that talks to it. A newly mapped hub is
    /// regenerated into the file with an empty <c>clients</c> list, because there is no way for this
    /// repository to know which Angular service will consume it - and an empty list is the one thing that
    /// makes the entry inert. <c>crucible-tests</c> generates its per-client checks by looping
    /// <c>clients</c>, so a hub with none is in the shared list, reads as covered, and is compared to
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only hole in the pair of suites that fails silently in both directions. A hub with
    /// broadcasts would at least fail <c>every message it broadcasts is listened for by some client</c> on
    /// the other side, against an empty set of listeners; a hub whose messages all go the other way - only
    /// invocations - would pass everything either suite has to say about it. So the check is here rather
    /// than there, and for the same reason it is on the <em>contract</em> rather than on the endpoints: it
    /// is the file's completeness that is in question, and this repository's pipeline is the one that runs
    /// on the commit that changed it.
    /// </para>
    /// <para>
    /// A hub with genuinely no browser client goes in <see cref="HubsWithNoBrowserClient"/>, which is empty
    /// today. Deliberately a line of code rather than a convention about empty lists: an empty list in the
    /// file cannot be told apart from one nobody has filled in, and a constant is something a reader of
    /// the diff has to walk past.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryHubInTheContract_NamesAClientThatTalksToIt()
    {
        Assert.Equal<string>(
            [],
            [.. Contracts.SignalR.Hubs
                .Where(x => x.Clients.Count == 0 && !HubsWithNoBrowserClient.Contains(x.HubType))
                .Select(x => x.HubType)
                .Order()]);
    }

    /// <summary>
    /// The hubs no browser client consumes, so that an empty <c>clients</c> list in the file is a decision
    /// rather than an omission.
    /// </summary>
    private static readonly string[] HubsWithNoBrowserClient = [];

    #endregion

    #region What clients invoke

    public static TheoryData<string> EveryHubName =>
        new(Contracts.SignalR.Hubs.Select(x => x.Name));

    /// <summary>
    /// Every method a client may invoke, with the number of arguments it takes. Both halves matter:
    /// SignalR dispatches on name <em>and</em> arity, so an argument added to a hub method breaks every
    /// caller that has not been changed with it, and does it as a failed invocation rather than as
    /// anything the connection notices.
    /// </summary>
    /// <remarks>
    /// Reflected over the mapped hub type rather than listed, so a method added to a hub fails here
    /// until it is either added to the contract or made non-public. That direction is the valuable one:
    /// a new hub method with no client is harmless, but a new hub method nobody wrote down is how the
    /// list stops being the list.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryHubName))]
    public void EachHub_DeclaresExactlyTheInvocationsTheContractLists(string hubName)
    {
        var hub = Contracts.SignalR.Hub(hubName);

        Assert.Equal<string>(
            [.. hub.Invocations.Select(x => $"{x.Name}/{x.Arguments}").Order()],
            [.. InvocableMethods(Type.GetType($"{hub.HubType}, Player.Vm.Api"))
                .Select(x => $"{x.Name}/{x.GetParameters().Length}")
                .Order()]);
    }

    /// <summary>
    /// The two invocations that answer with something, and the JSON keys of what they answer with.
    /// <c>JoinViewUsers</c> is the one that matters: <c>vm.ui</c> takes its return value and splits it
    /// into two stores, reading <c>id</c> and <c>name</c> for the team and <c>users</c> for the members,
    /// so a property renamed on either type is a pair of empty stores rather than an error.
    /// </summary>
    /// <remarks>
    /// Taken from the serializer's own type metadata rather than from the CLR properties, so the naming
    /// policy and any <c>[JsonIgnore]</c> are the application's. <c>JoinUser</c>'s return value is used
    /// by nothing today, which is worth having written down rather than discovered.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryHubName))]
    public void EachInvocationThatAnswers_AnswersWithTheKeysTheContractLists(string hubName)
    {
        var hub = Contracts.SignalR.Hub(hubName);
        var hubType = Type.GetType($"{hub.HubType}, Player.Vm.Api");

        foreach (var invocation in hub.Invocations.Where(x => x.Returns is not null))
        {
            var returned = Unwrap(
                InvocableMethods(hubType).Single(x => x.Name == invocation.Name).ReturnType);
            var element = ElementOf(returned);

            Assert.Equal(invocation.Returns.Collection, element is not null);

            Assert.Equal<string>(
                [.. invocation.Returns.Keys],
                [.. JsonKeysOf(element ?? returned)]);
        }
    }

    #endregion

    #region What the server broadcasts

    /// <summary>
    /// Every message the application sends to a <c>VmHub</c> client, how many arguments each carries, and
    /// which producers send it. A client's handler may bind fewer arguments than are sent - SignalR drops
    /// the rest - but one that binds more gets <c>undefined</c>, so the numbers here are the ceiling the
    /// client side is checked against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven, not read off <c>VmHubMethods</c>. Every producer in the application is exercised once:
    /// the four entity-event handlers through the hub context they are given, and the hub itself through
    /// its own <c>Clients</c>. A producer left out would take its message out of this set, so the
    /// assertion is as complete as the list of drives below - which is why they are all in one test
    /// rather than one each.
    /// </para>
    /// <para>
    /// <c>VmCreated</c> is the reason arity is a list rather than a number. It has two producers and they
    /// disagree: <c>VmCreatedSignalRHandler</c> shares <c>VmBaseSignalRHandler</c>'s send with
    /// <c>VmUpdated</c> and so passes a null second argument, while <c>VmTeamCreatedSignalRHandler</c>
    /// sends the Vm alone. Nothing is wrong with that, but a client binding two arguments to
    /// <c>VmCreated</c> would get <c>undefined</c> for the second one half the time, which is exactly the
    /// kind of thing only a comparison of the two sides finds.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryBroadcastTheVmHubSends_CarriesTheArgumentCountsTheContractLists()
    {
        var sent = await DriveEveryVmHubProducer();

        Assert.Equal<string>(
            [.. Contracts.SignalR.Hub("vm").Broadcasts
                .Select(x => $"{x.Name}({string.Join(",", x.Arguments.Order())}) from {Names(x.SentBy)}")
                .Order()],
            [.. sent
                .Select(x => $"{x.Name}({string.Join(",", x.Arguments)}) from {Names(x.SentBy)}")
                .Order()]);
    }

    /// <summary>
    /// The <c>VmHubMethods</c> constants, which are what the application writes when it broadcasts, hold
    /// nothing the contract has not got. Read against the test above: that one proves the contract lists
    /// nothing the application does not send, and this one proves the application declares nothing the
    /// contract has not got - a constant added and then broadcast from a path no test drives.
    /// </summary>
    [Fact]
    public void TheVmHubsBroadcastConstants_AreAllInTheContract()
    {
        Assert.Equal<string>(
            [.. Contracts.SignalR.Hub("vm").Broadcasts.Select(x => x.Name).Order()],
            [.. typeof(VmHubMethods)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(x => x.IsLiteral && x.FieldType == typeof(string))
                .Select(x => (string)x.GetRawConstantValue())
                .Order()]);
    }

    /// <summary>
    /// The one broadcast in the application with no constant behind it. Both task pollers write
    /// <c>"Progress"</c> as a literal - <c>Domain/Vsphere/Services/TaskService.cs</c> and
    /// <c>Domain/Proxmox/Services/ProxmoxTaskService.cs</c> - and both send exactly one argument, the
    /// notification.
    /// </summary>
    /// <remarks>
    /// Pinned against a literal here rather than driven, which is the one place this class falls short of
    /// the standard the rest of it holds to. Driving either poller means its whole harness - a fake
    /// cluster or a fake vCenter, a <see cref="PollLoop"/>, and an options monitor - for a fact those
    /// harnesses already establish: <c>TaskServiceTests</c> and <c>ProxmoxTaskServiceTests</c> both
    /// assert the send, each against its own copy of the same literal. What is added here is putting the
    /// name in the shared list, so the Angular client is checked against it.
    /// </remarks>
    [Fact]
    public void TheProgressBroadcast_IsTheLiteralBothPollersSend()
    {
        var progress = Assert.Single(Contracts.SignalR.Hub("progress").Broadcasts);

        Assert.Equal("Progress", progress.Name);
        Assert.Equal<int>([1], progress.Arguments);
    }

    #endregion

    #region modifiedProperties

    /// <summary>
    /// The names <c>VmUpdated</c> can carry are exactly the scalar properties of the Vm entity, camelCased
    /// - which is what <c>VmUpdatedSignalRHandler</c> does to whatever EF's change tracker reported.
    /// </summary>
    /// <remarks>
    /// Read off the model rather than the class, so a property added to the entity fails here: the whole
    /// chain from a column to a browser field is name-matched and unchecked, and this is the end of it
    /// nothing else looks at. Navigations are excluded by EF itself -
    /// <c>TrackedEntityEntry</c> builds its modified set from <c>EntityEntry.Properties</c> - which is
    /// why <c>VmTeams</c> and <c>ProxmoxVmInfo</c> are absent and why the keys they feed are in
    /// <c>neverSent</c> below.
    /// </remarks>
    [Fact]
    public void TheContractsModifiedProperties_AreTheCamelCasedScalarsOfTheVmEntity()
    {
        Assert.Equal<string>(
            [.. Contracts.SignalR.ModifiedProperties.Names.Order()],
            [.. ModifiedPropertyNames().Order()]);
    }

    /// <summary>
    /// And every one of them is a key of the Vm the same message carries. This is the assertion the whole
    /// <c>modifiedProperties</c> mechanism rests on: <c>vm.ui</c> applies an update by looping over the
    /// names and doing <c>model[x] = vm[x]</c> against the <em>serialized DTO</em>, so a name with no
    /// matching JSON key does not fail - it writes <c>undefined</c> over a value that was correct.
    /// </summary>
    /// <remarks>
    /// The two sides of it are an entity and a DTO mapped between by AutoMapper, and neither the mapping
    /// profile nor the serializer requires them to agree. Renaming <c>Vm.Url</c> to <c>Vm.ConsoleUrl</c>
    /// on the entity while leaving the DTO alone compiles, passes the mapping tests, and blanks the
    /// console link in every open browser on the next power-state change.
    /// </remarks>
    [Fact]
    public void EveryNameModifiedPropertiesCanSend_IsAJsonKeyOfTheVmTheClientIndexes()
    {
        var keys = JsonKeysOf(typeof(VmDto));

        Assert.All(
            Contracts.SignalR.ModifiedProperties.Names,
            name => Assert.Contains(name, keys));
    }

    /// <summary>
    /// The other direction, recorded rather than fixed: the keys of the Vm DTO that no scalar property
    /// backs, so no update ever names them. A client applying only what <c>modifiedProperties</c> lists
    /// will never see <c>teamIds</c>, <c>proxmoxVmInfo</c> or <c>defaultUrl</c> change, and has to take
    /// them from the whole Vm in the first argument instead.
    /// </summary>
    /// <remarks>
    /// Worth pinning because it is the quiet half of the mechanism. A team change does reach clients, as
    /// a <c>VmCreated</c> or <c>VmDeleted</c> from <c>VmTeamUpdatedSignalRHandler</c> rather than as an
    /// update - so the gap is covered, but by a different message with a different arity, and nothing
    /// says so anywhere else.
    /// </remarks>
    [Fact]
    public void TheKeysNoUpdateEverNames_AreTheOnesTheContractRecords()
    {
        Assert.Equal<string>(
            [.. Contracts.SignalR.ModifiedProperties.NeverSent.Keys.Order()],
            [.. JsonKeysOf(typeof(VmDto))
                .Except(Contracts.SignalR.ModifiedProperties.Names)
                .Order()]);
    }

    #endregion

    #region Generating the file

    /// <summary>
    /// The file is generated, and this is what generates it. Everything in
    /// <c>contracts/signalr-contract.json</c> that the application can be asked for is taken from the
    /// application: the hubs it maps and where, the methods each hub declares and their arities, what the
    /// two answering invocations answer with, the messages the real producers broadcast with their
    /// argument counts and which producer sends each, and the <c>modifiedProperties</c> names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Regenerate with
    /// <c>VMAPI_UPDATE_CONTRACTS=1 dotnet test --filter FullyQualifiedName~ContractTests</c>, read the
    /// diff, and re-run without the variable. The other tests in this class are what name the thing that
    /// moved; this one is what makes writing it down mechanical rather than a matter of remembering to.
    /// They are kept alongside it deliberately - a bug in the generation below would leave this test green
    /// against a file the focused ones fail, which is the failure mode a lone generated snapshot has no
    /// answer for.
    /// </para>
    /// <para>
    /// It regenerates into the committed document rather than emitting a fresh one, because the file is
    /// read by people on the other side of the estate. Four things in it are not derivable from here and
    /// are carried forward untouched: the descriptions, the per-entry <c>note</c> prose, the <c>clients</c>
    /// lists naming which Angular service talks to which hub, and <c>clientListenersWithNoSender</c>. All
    /// four are facts about the browser clients or about why an entry is the shape it is, and a repository
    /// that cannot see those clients cannot generate them. So is <c>ProgressHub</c>'s one broadcast - see
    /// <see cref="HubsWhoseBroadcastsAreDriven"/>.
    /// </para>
    /// <para>
    /// Entry order is the committed order, with anything new appended in name order. The Join/Leave pairs
    /// are grouped the way a person grouped them, and a regeneration that sorted them would produce a diff
    /// nobody could read the first time it ran.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheContract_IsWhatTheApplicationGenerates()
    {
        var committed = await Contracts.ReadDocument(Contracts.SignalRFileName, Ct);

        await Contracts.AssertMatchesOrRewrite(
            Contracts.SignalRFileName, Contracts.Render(await Regenerate(committed)), Ct);
    }

    /// <summary>
    /// The hubs whose broadcasts are taken from the application rather than carried forward from the
    /// committed file.
    /// </summary>
    /// <remarks>
    /// <c>VmHub</c> only, and this is the one place the file is still written by hand.
    /// <c>ProgressHub</c>'s single message has no constant and no event handler behind it - both task
    /// pollers write the literal - and driving either poller means standing up its whole harness, a fake
    /// cluster or a fake vCenter with a <c>PollLoop</c> and an options monitor, for a fact
    /// <c>TaskServiceTests</c> and <c>ProxmoxTaskServiceTests</c> already establish.
    /// <see cref="TheProgressBroadcast_IsTheLiteralBothPollersSend"/> is what keeps that entry honest, and
    /// it is named here so the exception is a line of code rather than something a reader has to notice.
    /// </remarks>
    private static readonly string[] HubsWhoseBroadcastsAreDriven = [typeof(VmHub).FullName];

    /// <summary>The committed document with every derivable field replaced by what the application says.</summary>
    private async Task<JsonObject> Regenerate(JsonObject committed)
    {
        var mapped = MappedHubs();
        var driven = await DriveEveryVmHubProducer();
        var hubs = new JsonArray();

        foreach (var (hubType, entry) in InCommittedOrder(committed["hubs"], "hubType", mapped.Keys))
        {
            hubs.Add(RegenerateHub(entry, hubType, mapped[hubType], driven));
        }

        committed["hubs"] = hubs;
        committed["modifiedProperties"]["names"] = Strings(ModifiedPropertyNames().Order());
        committed["modifiedProperties"]["neverSent"]["keys"] =
            Strings(JsonKeysOf(typeof(VmDto)).Except(ModifiedPropertyNames()).Order());

        return committed;
    }

    /// <summary>One hub: the mapped path and type, the methods it declares, and what it broadcasts.</summary>
    private JsonObject RegenerateHub(
        JsonObject committed, string hubType, string path, IReadOnlyList<ObservedBroadcast> driven) =>
        new()
        {
            // The short name is the file's own label and appears nowhere else, so it is carried forward.
            // A newly mapped hub is named after the last segment of its path, which is what both hubs
            // here are already named after.
            ["name"] = Carry(committed, "name") ?? path.Split('/')[^1],
            ["path"] = path,
            ["hubType"] = hubType,
            ["clients"] = Carry(committed, "clients") ?? new JsonArray(),
            ["invocations"] = RegenerateInvocations(
                committed?["invocations"], Type.GetType($"{hubType}, Player.Vm.Api")),
            ["broadcasts"] = HubsWhoseBroadcastsAreDriven.Contains(hubType)
                ? RegenerateBroadcasts(committed?["broadcasts"], driven)
                : Carry(committed, "broadcasts") ?? new JsonArray(),
            ["clientListenersWithNoSender"] =
                Carry(committed, "clientListenersWithNoSender") ?? new JsonArray(),
        };

    /// <summary>
    /// Every method SignalR will dispatch an invocation to, with its arity and - for the ones that answer
    /// with something - the JSON keys of what they answer with.
    /// </summary>
    /// <remarks>
    /// Keyed by name alone, so an overload would fail the regeneration here. That is the right place for
    /// it to fail: SignalR cannot dispatch an overloaded hub method either, and it throws at startup.
    /// </remarks>
    private JsonArray RegenerateInvocations(JsonNode committed, Type hubType)
    {
        var methods = InvocableMethods(hubType).ToDictionary(x => x.Name);
        var invocations = new JsonArray();

        foreach (var (name, entry) in InCommittedOrder(committed, "name", methods.Keys))
        {
            var invocation = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = methods[name].GetParameters().Length,
            };

            if (Answer(methods[name]) is (bool collection, IReadOnlyCollection<string> keys))
            {
                invocation["returns"] = new JsonObject
                {
                    ["collection"] = collection,
                    ["keys"] = Strings(keys),
                };
            }

            Add(invocation, "note", Carry(entry, "note"));
            invocations.Add(invocation);
        }

        return invocations;
    }

    /// <summary>Every message the driven producers sent, with its argument counts and its senders.</summary>
    private static JsonArray RegenerateBroadcasts(
        JsonNode committed, IReadOnlyList<ObservedBroadcast> driven)
    {
        var byName = driven.ToDictionary(x => x.Name);
        var broadcasts = new JsonArray();

        foreach (var (name, entry) in InCommittedOrder(committed, "name", byName.Keys))
        {
            var broadcast = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = Numbers(byName[name].Arguments),
                ["sentBy"] = Strings(byName[name].SentBy),
            };

            Add(broadcast, "note", Carry(entry, "note"));
            broadcasts.Add(broadcast);
        }

        return broadcasts;
    }

    /// <summary>
    /// <paramref name="names"/> paired with the committed entry to carry prose forward from, in the order
    /// the committed entries hold them; then the names the file has not got yet, in name order, with no
    /// entry behind them. Names the file has and the application no longer does simply do not appear.
    /// </summary>
    private static IEnumerable<(string Name, JsonObject Committed)> InCommittedOrder(
        JsonNode committed, string key, IEnumerable<string> names)
    {
        List<JsonObject> entries = committed is JsonArray array ? [.. array.OfType<JsonObject>()] : [];
        var wanted = names.ToHashSet();

        foreach (var entry in entries.Where(x => wanted.Contains(x[key].GetValue<string>())))
        {
            yield return (entry[key].GetValue<string>(), entry);
        }

        foreach (var name in wanted.Except(entries.Select(x => x[key].GetValue<string>())).Order())
        {
            yield return (name, null);
        }
    }

    /// <summary>
    /// A value taken out of the committed document to be put into the regenerated one. Cloned, because a
    /// node belongs to one parent and the regenerated document is a different one.
    /// </summary>
    private static JsonNode Carry(JsonObject committed, string key) => committed?[key]?.DeepClone();

    /// <summary>
    /// Sets a key only when there is something to set, so an entry with no note has no <c>note</c> key
    /// rather than a null one.
    /// </summary>
    private static void Add(JsonObject target, string key, JsonNode value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The methods SignalR will dispatch an invocation to: declared on the hub itself, public, and not an
    /// override of a <see cref="Hub"/> lifetime method.
    /// </summary>
    /// <remarks>
    /// <c>OnDisconnectedAsync</c> is the one that has to come out. It is declared on <c>VmHub</c> and is
    /// public, but it is the framework's own and no client invokes it -
    /// <c>GetBaseDefinition</c> is what tells the two apart.
    /// </remarks>
    private static IEnumerable<MethodInfo> InvocableMethods(Type hubType) =>
        hubType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(x => !x.IsSpecialName)
            .Where(x => x.GetBaseDefinition().DeclaringType == x.DeclaringType);

    /// <summary>The JSON keys a type serializes as, taken from the application's own serializer.</summary>
    private IReadOnlyCollection<string> JsonKeysOf(Type type) =>
        [.. JsonOptions.GetTypeInfo(type).Properties.Select(x => x.Name)];

    /// <summary>
    /// The names <c>VmUpdated</c> can carry: the scalar properties of the Vm entity, camelCased, which is
    /// what <c>VmUpdatedSignalRHandler</c> does to whatever EF's change tracker reported.
    /// </summary>
    private IEnumerable<string> ModifiedPropertyNames() =>
        Db.Model.FindEntityType(typeof(VmEntity)).GetProperties()
            .Select(x => x.Name.TitleCaseToCamelCase());

    /// <summary>
    /// What an invocation answers with - whether it is a collection, and the JSON keys of one element of
    /// it - or null when it answers with nothing.
    /// </summary>
    private (bool Collection, IReadOnlyCollection<string> Keys)? Answer(MethodInfo method)
    {
        var returned = Unwrap(method.ReturnType);

        if (returned == typeof(void) || returned == typeof(Task))
        {
            return null;
        }

        var element = ElementOf(returned);

        return (element is not null, JsonKeysOf(element ?? returned));
    }

    /// <summary>The awaited result of a hub method's return type.</summary>
    private static Type Unwrap(Type returnType) =>
        returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

    /// <summary>
    /// What a collection returns one of, or null when it is not a collection. The type itself is checked
    /// as well as its interfaces, because <c>JoinViewUsers</c> is declared as
    /// <c>Task&lt;IEnumerable&lt;VmUserTeam&gt;&gt;</c> and an interface does not list itself among the
    /// interfaces it implements.
    /// </summary>
    private static Type ElementOf(Type type) =>
        (type == typeof(string) ? null : new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        ?.GetGenericArguments()[0];

    /// <summary>
    /// Every producer of a <c>VmHub</c> message, run once, as one entry per broadcast name: the argument
    /// counts it went out with, and the producers observed sending it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One Vm on one team of one view, which is the smallest arrangement in which every handler sends
    /// something: the base handler needs a group to send to, and it gets those from the Vm's teams and
    /// the view each team resolves to.
    /// </para>
    /// <para>
    /// Each drive is bracketed so the sends it produced are attributed to it, which is where the
    /// <c>sentBy</c> lists in the file come from. Attributing them is worth the bracketing: <c>sentBy</c>
    /// is the part of an entry most likely to go stale, because a handler can be split or renamed without
    /// changing anything about what goes on the wire.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ObservedBroadcast>> DriveEveryVmHubProducer()
    {
        var (viewId, teamId) = (Guid.NewGuid(), Guid.NewGuid());
        _views.GetViewIdForTeam(teamId, Arg.Any<CancellationToken>()).Returns(viewId);

        var entity = new VmEntity { Name = "vm", VmTeams = [new VmTeam { TeamId = teamId }] };
        await Seed(entity);
        var teamRow = entity.VmTeams.Single();

        var observed = new List<(string Producer, string Method, int Arity)>();

        async Task Drive(string producer, Func<Task> drive)
        {
            var (handlers, hub) = (_fromHandlers.Sends.Count, _fromHub.Sends.Count);

            await drive();

            observed.AddRange(_fromHandlers.Sends.Skip(handlers).Concat(_fromHub.Sends.Skip(hub))
                .Select(x => (producer, x.Method, x.Args.Length)));
        }

        await Drive(
            nameof(VmCreatedSignalRHandler),
            () => new VmCreatedSignalRHandler(Db, TestMapper.Value, _views, _fromHandlers.Context)
                .Handle(new EntityCreated<VmEntity>(entity), Ct));
        await Drive(
            nameof(VmUpdatedSignalRHandler),
            () => new VmUpdatedSignalRHandler(Db, TestMapper.Value, _views, _fromHandlers.Context)
                .Handle(new EntityUpdated<VmEntity>(entity, ["Name"]), Ct));
        await Drive(
            nameof(VmDeletedSignalRHandler),
            () => new VmDeletedSignalRHandler(Db, TestMapper.Value, _views, _fromHandlers.Context)
                .Handle(new EntityDeleted<VmEntity>(entity), Ct));
        await Drive(
            nameof(VmTeamCreatedSignalRHandler),
            () => new VmTeamCreatedSignalRHandler(Db, TestMapper.Value, _views, _fromHandlers.Context)
                .Handle(new EntityCreated<VmTeam>(teamRow), Ct));
        await Drive(
            nameof(VmTeamDeletedSignalRHandler),
            () => new VmTeamDeletedSignalRHandler(Db, TestMapper.Value, _views, _fromHandlers.Context)
                .Handle(new EntityDeleted<VmTeam>(teamRow), Ct));
        await Drive(nameof(VmHub), () => DriveTheHubsOwnBroadcasts(entity, viewId, teamId));

        return
        [
            .. observed
                .GroupBy(x => x.Method)
                .Select(x => new ObservedBroadcast(
                    x.Key,
                    [.. x.Select(y => y.Arity).Distinct().Order()],
                    [.. x.Select(y => y.Producer).Distinct().Order()])),
        ];
    }

    /// <summary>
    /// <c>SetActiveVirtualMachine</c> and <c>UnsetActiveVirtualMachine</c>, which between them are every
    /// broadcast the hub makes itself. Both are driven because the set is broadcast with a vm id and the
    /// unset with a null in its place, and the argument count is the thing being counted.
    /// </summary>
    private async Task DriveTheHubsOwnBroadcasts(VmEntity entity, Guid viewId, Guid teamId)
    {
        var vm = new VmDto { Id = entity.Id, Name = entity.Name, TeamIds = [teamId], IpAddresses = [] };
        _vms.GetAsync(vm.Id, Arg.Any<CancellationToken>()).Returns(vm);
        _views.GetViewIdsForTeams(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([viewId]);
        _player.GetVisibilityContextAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(new VisibilityContext(teamId, false, [teamId]));

        await Hub().SetActiveVirtualMachine(vm.Id);
        await Hub().UnsetActiveVirtualMachine();
    }

    /// <summary>
    /// A hub over the shared active-machine store and a context of its own, for the reason
    /// <c>VmHubPresenceTests</c> gives: the row write attaches a fresh <c>VmUser</c> every call, so two
    /// calls sharing a change tracker throw on the second attach.
    /// </summary>
    private VmHub Hub()
    {
        var context = NewContext();
        _contexts.Add(context);

        return _fromHub.Attach(new VmHub(_active, _usageLog, _views, _player, _vms, context));
    }

    /// <summary>Names in a fixed order, for a message a person reads when the assertion above fails.</summary>
    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Order());

    private static JsonArray Strings(IEnumerable<string> values) => [.. values.Select(x => (JsonNode)x)];

    private static JsonArray Numbers(IEnumerable<int> values) => [.. values.Select(x => (JsonNode)x)];

    /// <summary>One message the application broadcasts, as the driven producers were observed sending it.</summary>
    private sealed record ObservedBroadcast(string Name, int[] Arguments, string[] SentBy);

    #endregion
}
