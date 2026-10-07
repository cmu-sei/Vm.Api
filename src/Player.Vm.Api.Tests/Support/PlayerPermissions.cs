// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute.Extensions;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// The permissions a caller holds in player.api, answered consistently by a substituted
/// <see cref="IPlayerService"/>: every permission question the application asks gets the answer the real
/// <c>PlayerService</c> would give for exactly these grants.
/// </summary>
/// <remarks>
/// <para>
/// vm.api keeps no permissions of its own, so this is what a near miss is made of: a caller holding the
/// permission next to the one a gate asks for (<c>Holding(AppViewPermission.ViewViewMaps)</c> where the gate
/// asks for <c>ManageViewMaps</c>), or the right permission on another team or View. Every question is
/// answered from the same grants, so a gate that asked the wrong question would let the near miss through,
/// which is what a denied test is there to catch. Stubbing only the one question a test expects
/// (<c>CanManageMaps(...).Returns(false)</c> over an otherwise unstubbed substitute) cannot tell.
/// </para>
/// <para>
/// The model mirrors <c>PlayerService.Can</c>: a system permission counts everywhere; a View permission
/// counts for a question about that View, or about a team the test says is in it (<see cref="InView"/>);
/// a team permission counts for a question naming that team. The <c>Can*</c> shorthands are answered with
/// the permission arrays <c>PlayerService</c> passes for them. <c>Configure()</c> keeps an earlier arrangement
/// (<c>AllowEverything</c>) from running while these replace it. Visibility (<c>GetVisibilityContext*</c>)
/// is not modelled: tests arrange it themselves, because it is a membership, not a permission.
/// </para>
/// </remarks>
public sealed class PlayerPermissions
{
    private readonly HashSet<AppSystemPermission> _system = [];
    private readonly List<(Guid? ViewId, AppViewPermission Permission)> _view = [];
    private readonly List<(Guid? TeamId, AppTeamPermission Permission)> _team = [];
    private readonly Dictionary<Guid, Guid> _teamViews = [];

    /// <summary>System permissions, which grant across every team and View.</summary>
    public PlayerPermissions Holding(params AppSystemPermission[] permissions)
    {
        _system.UnionWith(permissions);
        return this;
    }

    /// <summary>View permissions on every View the question names, or on <paramref name="viewId"/> only.</summary>
    public PlayerPermissions Holding(AppViewPermission permission, Guid? viewId = null)
    {
        _view.Add((viewId, permission));
        return this;
    }

    /// <summary>Team permissions on every team the question names, or on <paramref name="teamId"/> only.</summary>
    public PlayerPermissions Holding(AppTeamPermission permission, Guid? teamId = null)
    {
        _team.Add((teamId, permission));
        return this;
    }

    /// <summary>Places a team in a View, so a View permission scoped to that View counts for a question about the team.</summary>
    public PlayerPermissions InView(Guid teamId, Guid viewId)
    {
        _teamViews[teamId] = viewId;
        return this;
    }

    /// <summary>
    /// Answers every permission question <paramref name="player"/> is asked from these grants, replacing
    /// whatever those members were arranged to answer before. Visibility and lookups are left alone.
    /// </summary>
    public void AnswerFor(IPlayerService player)
    {
        ArgumentNullException.ThrowIfNull(player);

        player.Configure().Can(default, default, default, default, default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(Can(
                ci.ArgAt<IEnumerable<Guid>>(0), ci.ArgAt<IEnumerable<Guid>>(1),
                ci.ArgAt<AppSystemPermission[]>(2), ci.ArgAt<AppViewPermission[]>(3), ci.ArgAt<AppTeamPermission[]>(4))));

        // The arrays PlayerService passes for each shorthand (Domain/Services/PlayerService.cs).
        player.Configure().CanManageTeams(default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(Can(
                ci.ArgAt<IEnumerable<Guid>>(0), null,
                [AppSystemPermission.ManageViews], [AppViewPermission.ManageView], [AppTeamPermission.ManageTeam])));
        player.Configure().CanViewVms(default, default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(Can(
                ci.ArgAt<IEnumerable<Guid>>(0), ci.ArgAt<IEnumerable<Guid>>(1),
                AppPermissions.VmReadSystem, AppPermissions.VmReadView, AppPermissions.VmReadTeam)));
        player.Configure().CanControlVms(default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(Can(
                ci.ArgAt<IEnumerable<Guid>>(0), null,
                [AppSystemPermission.ControlVms], [AppViewPermission.ControlViewVms], [AppTeamPermission.ControlTeamVms])));
        player.Configure().CanViewMaps(default, default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(Can(
                ci.ArgAt<IEnumerable<Guid>>(0), ci.ArgAt<IEnumerable<Guid>>(1),
                AppPermissions.MapReadSystem, AppPermissions.MapReadView, AppPermissions.MapReadTeam)));
        player.Configure().CanManageMaps(default, default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(Can(
                ci.ArgAt<IEnumerable<Guid>>(0), ci.ArgAt<IEnumerable<Guid>>(1),
                [AppSystemPermission.ManageMaps], [AppViewPermission.ManageViewMaps], [AppTeamPermission.ManageTeamMaps])));
        player.Configure().HasViewNetworkAccess(default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(Can(
                ci.ArgAt<IEnumerable<Guid>>(0), null,
                [AppSystemPermission.ViewNetworks, AppSystemPermission.ManageNetworks],
                [AppViewPermission.ViewNetworks, AppViewPermission.ManageNetworks], [])));
        player.Configure().HasManageNetworkAccess(default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(Can(
                ci.ArgAt<IEnumerable<Guid>>(0), null,
                [AppSystemPermission.ManageNetworks], [AppViewPermission.ManageNetworks], [])));
        player.Configure().GetSystemPermissionsAsync(default)
            .ReturnsForAnyArgs(_ => Task.FromResult<IReadOnlySet<AppSystemPermission>>(_system.ToHashSet()));
        player.Configure().GetTeamIdsWithPermissionAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(ci =>
            {
                var viewId = ci.ArgAt<Guid>(0);
                var teams = ci.ArgAt<IEnumerable<Guid>>(1) ?? [];
                var (system, view, team) = (ci.ArgAt<AppSystemPermission[]>(2), ci.ArgAt<AppViewPermission[]>(3), ci.ArgAt<AppTeamPermission[]>(4));

                // A system or View-level grant covers every team of the View.
                IReadOnlySet<Guid> answer = Can([], [viewId], system, view, [])
                    ? teams.ToHashSet()
                    : teams.Where(x => Can([x], null, [], [], team)).ToHashSet();

                return Task.FromResult(answer);
            });
    }

    /// <summary>What <c>PlayerService.Can</c> answers for these grants.</summary>
    public bool Can(
        IEnumerable<Guid> teamIds,
        IEnumerable<Guid> viewIds,
        AppSystemPermission[] system,
        AppViewPermission[] view,
        AppTeamPermission[] team)
    {
        var teams = (teamIds ?? []).Where(x => x != Guid.Empty).Distinct().ToArray();
        var views = (viewIds ?? []).Where(x => x != Guid.Empty)
            .Concat(teams.Where(_teamViews.ContainsKey).Select(x => _teamViews[x]))
            .Distinct()
            .ToArray();

        if ((system ?? []).Any(_system.Contains))
        {
            return true;
        }

        var askedAboutAView = views.Length > 0 || teams.Length > 0;

        if (askedAboutAView && _view.Any(g => (view ?? []).Contains(g.Permission) && (g.ViewId is null || views.Contains(g.ViewId.Value))))
        {
            return true;
        }

        return teams.Length > 0 &&
            _team.Any(g => (team ?? []).Contains(g.Permission) && (g.TeamId is null || teams.Contains(g.TeamId.Value)));
    }
}
