// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Player.Api.Client;
using Player.Vm.Api.Features.Files;
using Player.Vm.Api.Features.Files.Models;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The ISO endpoints in process, with the storage on the far side of <c>IIsoProvider</c> substituted and
/// everything up to it real.
///
/// The ISO service is covered thoroughly at the unit level already - <see cref="IsoWriteAuthTests"/>,
/// <see cref="IsoServiceMergeTests"/>, <see cref="IsoServiceFanOutTests"/> and the two provider classes -
/// so this deliberately does not restate the permission matrix or the merge. What is only reachable over
/// a real request is the upload itself: <c>FileController.Upload</c> reads a multipart form by hand rather
/// than binding a model, so its four rejections, the size ceiling and the team-id parsing have no other
/// caller. The rest is route location, exception-to-status mapping and the 401.
/// </summary>
public class FilesEndpointTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    private readonly Guid _viewId = Guid.NewGuid();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        Factory.PlayerApi.ClearSubstitute();
        Factory.IsoProvider.ClearSubstitute();

        Factory.AllowEverything();
        Factory.EnableIsoProvider();
    }

    #region Upload: the form the controller reads by hand

    [Fact]
    public async Task Upload_Returns200AndWritesToTheProvider()
    {
        var response = await Client.PostAsync(IsoRoute, Form(file: "boot.iso"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<IsoUploadResult>(JsonOptions, Ct);
        Assert.False(result.PartialFailure);
        Assert.Equal(1, result.TotalHostCount);

        var request = UploadRequest();
        Assert.Equal(_viewId, request.ViewId);
        Assert.Equal("boot.iso", request.FileName);

        // "view" scope keys the folder on the view id; the team scopes are the case below.
        Assert.Equal<string>([_viewId.ToString()], request.ScopeIds);
    }

    /// <summary>
    /// The team ids arrive as a form field the controller hands to <c>ParseTeamIds</c>, which accepts
    /// repeated fields and comma-separated lists in either combination. Nothing but a real form post
    /// exercises the repeated-field half of that.
    /// </summary>
    [Theory]
    [InlineData("{a},{b}")]
    [InlineData("{a}", "{b}")]
    [InlineData("{a},{b}", "{a}")]
    public async Task Upload_ToTeamScope_TargetsEveryTeamIdInTheForm(params string[] teamIdFields)
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        Factory.PlayerApi.IsTeamInViewAsync(Arg.Any<Guid>(), _viewId, Arg.Any<CancellationToken>())
            .Returns(true);

        // Braced placeholders, because a Guid's own text contains plenty of bare a's and b's to
        // substitute into.
        var fields = teamIdFields
            .Select(x => x.Replace("{a}", teamA.ToString()).Replace("{b}", teamB.ToString()))
            .ToArray();

        var response = await Client.PostAsync(
            IsoRoute, Form(file: "boot.iso", scope: "team", teamIds: fields), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Distinct, in the order given: the folder per team is the whole point, and a duplicate would be
        // written twice.
        Assert.Equal<string>([teamA.ToString(), teamB.ToString()], UploadRequest().ScopeIds);
    }

    // No team ids means the caller's own primary team, resolved from player.api.
    [Fact]
    public async Task Upload_ToTeamScope_WithNoTeamIds_TargetsThePrimaryTeam()
    {
        var primary = Guid.NewGuid();
        Factory.PlayerApi.GetPrimaryTeamByViewIdAsync(_viewId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = primary, Name = "primary" });

        var response = await Client.PostAsync(IsoRoute, Form(file: "boot.iso", scope: "team"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal<string>([primary.ToString()], UploadRequest().ScopeIds);
    }

    /// <summary>
    /// The three fields the controller requires, each missing in turn. All three are 400s rather than
    /// 500s because the request is malformed, not the server broken - and a client that retried a 500
    /// here would retry a request that can never succeed.
    /// </summary>
    [Theory]
    [InlineData("no file")]
    [InlineData("no scope")]
    [InlineData("no size")]
    [InlineData("unparseable size")]
    public async Task Upload_WithAnIncompleteForm_Is400(string missing)
    {
        var content = missing switch
        {
            "no file" => Form(file: null),
            "no scope" => Form(file: "boot.iso", scope: null),
            "no size" => Form(file: "boot.iso", omitSize: true),
            _ => Form(file: "boot.iso", size: "not a number")
        };

        var response = await Client.PostAsync(IsoRoute, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await Factory.IsoProvider.DidNotReceive()
            .UploadAsync(Arg.Any<IsoUploadRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The size field is client-controlled, and the point of checking it is to refuse a huge upload before
    /// reading its body. So the rejection has to happen on the claimed size alone, with a body well under
    /// the limit - which is exactly what this posts.
    /// </summary>
    [Fact]
    public async Task Upload_ClaimingASizeOverTheLimit_Is400WithoutReadingTheBody()
    {
        var response = await Client.PostAsync(
            IsoRoute,
            Form(file: "boot.iso", size: (VmApiFactory.MaxIsoFileSize + 1).ToString()),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Named so this is distinguishable from the case below, which is also a 400 and comes from
        // somewhere else entirely.
        Assert.Contains(
            $"File exceeds the {VmApiFactory.MaxIsoFileSize} byte maximum size.",
            await response.Content.ReadAsStringAsync(Ct));

        await Factory.IsoProvider.DidNotReceive()
            .UploadAsync(Arg.Any<IsoUploadRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A body over the limit, with the size field understating it - a client that lies about the size to
    /// get past the cheap check. It is still refused, which is the thing worth knowing.
    /// </summary>
    /// <remarks>
    /// Where it is refused is worth pinning too, because it is not where the code reads as though it would
    /// be. The one configured maximum is also the multipart body length limit, so the form reader aborts
    /// while MVC is still building value providers - long before <c>UploadIso</c> can compare
    /// <c>file.Length</c>. So the caller gets the framework's model-state 400 rather than the handler's own
    /// message. Both are 400s; the body is how they are told apart, and the assertion below is on the body
    /// for that reason. <c>UploadIso</c>'s <c>file.Length</c> arm is not wrong to be there - it is just
    /// unreachable while the two limits are one value.
    /// </remarks>
    [Fact]
    public async Task Upload_WithABodyOverTheLimit_IsRefusedByTheFormReaderNotTheHandler()
    {
        var response = await Client.PostAsync(
            IsoRoute,
            Form(file: "boot.iso", bytes: new byte[VmApiFactory.MaxIsoFileSize + 1], size: "1"),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Failed to read the request form", body);
        Assert.DoesNotContain("byte maximum size", body);

        await Factory.IsoProvider.DidNotReceive()
            .UploadAsync(Arg.Any<IsoUploadRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The uploaded name is attacker-controlled and becomes a path segment on a datastore, so the name that
    /// reaches a provider has to be a single segment and nothing more. Asserted here rather than only
    /// against <c>SanitizeFilename</c> because the multipart layer is part of the story: nothing between
    /// the wire and the handler strips the directories out of a content-disposition filename, so the whole
    /// <c>../../etc/passwd.iso</c> really does arrive as <c>IFormFile.FileName</c>.
    /// </summary>
    /// <remarks>
    /// Asserted as "one path segment" rather than "contains no <c>..</c>", because the dots do survive:
    /// <c>SanitizeFilename</c> removes <c>Path.GetInvalidFileNameChars()</c>, which contains no <c>.</c> on
    /// any platform, so this name arrives as <c>....etcpasswd.iso</c>. That is harmless - a name with no
    /// separator in it cannot climb out of the folder it is joined to, whoever joins it - and it is what
    /// <see cref="AssertIsOnePathSegment"/> checks.
    /// </remarks>
    [Fact]
    public async Task Upload_WithATraversalInTheFilename_StripsItBeforeTheProvider()
    {
        var response = await Client.PostAsync(IsoRoute, Form(file: "../../etc/passwd.iso"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertIsOnePathSegment(UploadRequest().FileName);
    }

    // Anything that is not already an ISO is wrapped into one and gains the extension, so the name the
    // provider stores is not always the name that was uploaded.
    [Fact]
    public async Task Upload_OfAFileThatIsNotAnIso_StoresItWithTheIsoExtension()
    {
        var response = await Client.PostAsync(IsoRoute, Form(file: "notes.txt"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("notes.txt.iso", UploadRequest().FileName);
    }

    [Fact]
    public async Task Upload_WithoutTheUploadPermission_Is403()
    {
        DenyEverything();

        var response = await Client.PostAsync(IsoRoute, Form(file: "boot.iso"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await Factory.IsoProvider.DidNotReceive()
            .UploadAsync(Arg.Any<IsoUploadRequest>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Listing

    [Fact]
    public async Task ListView_ReturnsTheViewWideIsosAndOnePerTeam()
    {
        var teamId = Guid.NewGuid();
        ViewWithTeams(_viewId, (teamId, "red"));
        Holds((_viewId, "public.iso"), (teamId, "red-only.iso"));

        var result = await Get<ManagedIsoResult>(IsoRoute);

        Assert.Equal(_viewId, result.ViewId);
        Assert.Equal<string>(["public.iso"], result.Isos.Select(x => x.Filename));

        var team = Assert.Single(result.TeamIsoResults);
        Assert.Equal(teamId, team.TeamId);
        Assert.Equal("red", team.TeamName);
        Assert.Equal<string>(["red-only.iso"], team.Isos.Select(x => x.Filename));
    }

    [Fact]
    public async Task ListView_WithoutAViewOrTeamPermission_Is403()
    {
        ViewWithTeams(_viewId, (Guid.NewGuid(), "red"));
        DenyEverything();

        var response = await Client.GetAsync(IsoRoute, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // A caller with the permission but no team in the View is still refused: the listing is per-team, and
    // there is nothing here for them.
    [Fact]
    public async Task ListView_WhenTheCallerHasNoTeamInTheView_Is403()
    {
        ViewWithTeams(_viewId);

        var response = await Client.GetAsync(IsoRoute, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListAll_ReturnsOneResultPerView()
    {
        var other = Guid.NewGuid();
        Factory.PlayerApi.GetAllViewsAsync(Arg.Any<CancellationToken>())
            .Returns([View(_viewId, "first"), View(other, "second")]);
        Factory.PlayerApi.GetAllTeamsByViewIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var results = await Get<ManagedIsoResult[]>("/api/isos");

        // As a set: nothing on this path promises an order, and asserting one would fail on a change that
        // is not a defect.
        Assert.Equal<Guid>(
            new[] { _viewId, other }.Order(),
            results.Select(x => x.ViewId).Order());
    }

    /// <summary>
    /// The system-wide listing surfaces Views the caller is not a member of, so it is gated on a system
    /// permission rather than a View one - and that gate must be a 403 and not an empty list, which would
    /// read as "there are no Views".
    /// </summary>
    [Fact]
    public async Task ListAll_WithoutASystemPermission_Is403()
    {
        DenyEverything();

        var response = await Client.GetAsync("/api/isos", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Every provider failing to list is not an empty listing: an empty one is rendered as "no files",
    // which is indistinguishable from an upload that never landed.
    [Fact]
    public async Task ListView_WhenTheProviderCannotBeListed_Is500RatherThanEmpty()
    {
        ViewWithTeams(_viewId, (Guid.NewGuid(), "red"));
        Factory.IsoProvider.ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<Guid, IReadOnlyList<IsoListingEntry>>>(
                _ => throw new TimeoutException("datastore browser timed out"));

        var response = await Client.GetAsync(IsoRoute, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    #endregion

    #region Delete

    [Fact]
    public async Task Delete_Returns200AndRemovesFromTheProvider()
    {
        var response = await Client.DeleteAsync(
            $"{IsoRoute}?scope=view&filename=boot.iso", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await Factory.IsoProvider.Received(1)
            .DeleteAsync(_viewId, _viewId.ToString(), "boot.iso", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The filename on a delete comes off the query string, which is the least trustworthy place it could
    /// come from, and it is used to build a storage path. Same "one path segment" contract as the upload -
    /// see <see cref="Upload_WithATraversalInTheFilename_StripsItBeforeTheProvider"/> for why it is phrased
    /// that way rather than as an absence of <c>..</c>.
    /// </summary>
    [Fact]
    public async Task Delete_WithATraversalInTheFilename_StripsItBeforeTheProvider()
    {
        var response = await Client.DeleteAsync(
            $"{IsoRoute}?scope=view&filename={Uri.EscapeDataString("../../etc/passwd")}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertIsOnePathSegment(DeletedFilename());
    }

    [Fact]
    public async Task Delete_ForATeamThatIsNotInTheView_Is400()
    {
        Factory.PlayerApi.IsTeamInViewAsync(Arg.Any<Guid>(), _viewId, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await Client.DeleteAsync(
            $"{IsoRoute}?scope=team&filename=boot.iso&teamId={Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutADeletePermission_Is403AndRemovesNothing()
    {
        DenyEverything();

        var response = await Client.DeleteAsync($"{IsoRoute}?scope=view&filename=boot.iso", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await Factory.IsoProvider.DidNotReceive().DeleteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Authorization

    [Theory]
    [InlineData("POST", "views/{id}/isos")]
    [InlineData("GET", "views/{id}/isos")]
    [InlineData("GET", "isos")]
    [InlineData("DELETE", "views/{id}/isos")]
    public async Task EveryRoute_RejectsAnUnauthenticatedRequest(string method, string template)
    {
        var route = "/api/" + template.Replace("{id}", _viewId.ToString());

        using var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = Form(file: "boot.iso")
        };

        var response = await AnonymousClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// An install with no hypervisor configured for ISOs answers a write with a 400 naming the reason,
    /// rather than reporting a success that stored nothing.
    /// </summary>
    [Fact]
    public async Task Upload_WithNoProviderConfiguredForIsos_Is400()
    {
        Factory.IsoProvider.Enabled.Returns(false);

        var response = await Client.PostAsync(IsoRoute, Form(file: "boot.iso"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Helpers

    private string IsoRoute => $"/api/views/{_viewId}/isos";

    private async Task<T> Get<T>(string route)
    {
        var response = await Client.GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    /// <summary>
    /// A multipart body shaped the way the Files tab sends one. A null <paramref name="file"/> leaves the
    /// file part out entirely, a null <paramref name="scope"/> leaves that field out, and
    /// <paramref name="omitSize"/> leaves the size field out - which is what the controller's own
    /// rejections are about.
    /// </summary>
    /// <remarks>
    /// The omission of the size is a flag rather than a null <paramref name="size"/>, because null is
    /// already what "just tell the truth about the body" means, and it is what almost every caller wants:
    /// a test about the scope or the filename must not trip the size check on its way through.
    /// </remarks>
    private static MultipartFormDataContent Form(
        string file,
        string scope = "view",
        string size = null,
        byte[] bytes = null,
        bool omitSize = false,
        params string[] teamIds)
    {
        bytes ??= Encoding.UTF8.GetBytes("not really an iso");

        var content = new MultipartFormDataContent();

        if (file is not null)
        {
            content.Add(new ByteArrayContent(bytes), "file", file);
        }

        if (scope is not null)
        {
            content.Add(new StringContent(scope), "scope");
        }

        if (!omitSize)
        {
            content.Add(new StringContent(size ?? bytes.Length.ToString()), "size");
        }

        foreach (var teamId in teamIds)
        {
            content.Add(new StringContent(teamId), "teamIds");
        }

        return content;
    }

    /// <summary>
    /// That a name is one path segment and cannot escape whatever folder it is joined to: no separator of
    /// either flavor, and nothing a path parser would read as a directory.
    /// </summary>
    /// <remarks>
    /// Phrased against both separators and not just this platform's, because the name is going to a
    /// hypervisor's own path syntax, not to the test host's filesystem - a Linux API server writes to a
    /// vSphere datastore either way.
    /// </remarks>
    private static void AssertIsOnePathSegment(string name)
    {
        Assert.NotNull(name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.Equal(name, System.IO.Path.GetFileName(name));
        // "." and ".." are single segments that still mean a directory.
        Assert.DoesNotMatch(@"^\.+$", name);
    }

    /// <summary>The single upload the provider was asked to write.</summary>
    private IsoUploadRequest UploadRequest() =>
        (IsoUploadRequest)Factory.IsoProvider.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IIsoProvider.UploadAsync))
            .Select(x => x.GetArguments()[0])
            .Single();

    /// <summary>The name of the single ISO the provider was asked to delete.</summary>
    private string DeletedFilename() =>
        (string)Factory.IsoProvider.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IIsoProvider.DeleteAsync))
            .Select(x => x.GetArguments()[2])
            .Single();

    /// <summary>What player.api reports about the View: its identity and the caller's teams in it.</summary>
    private void ViewWithTeams(Guid viewId, params (Guid Id, string Name)[] teams)
    {
        Factory.PlayerApi.GetViewByIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(View(viewId, $"view-{viewId}"));
        Factory.PlayerApi.GetTeamsByViewIdAsync(viewId, Arg.Any<CancellationToken>())
            .Returns(teams.Select(x => new Team { Id = x.Id, Name = x.Name }).ToArray());
    }

    /// <summary>What the provider holds, keyed on scope - a view id for view-wide, else a team id.</summary>
    private void Holds(params (Guid ScopeId, string Filename)[] isos) =>
        Factory.IsoProvider.ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(isos.ToDictionary(
                x => x.ScopeId,
                x => (IReadOnlyList<IsoListingEntry>)[new IsoListingEntry(x.Filename, $"[ds] {x.Filename}")]));

    private static View View(Guid id, string name) => new() { Id = id, Name = name };

    private void DenyEverything() =>
        Factory.PlayerApi
            .Can(default, default, default, default, default, Ct)
            .ReturnsForAnyArgs(false);

    #endregion
}
