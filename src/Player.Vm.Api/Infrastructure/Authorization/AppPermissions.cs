// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Authorization;

public enum AppSystemPermission
{
    ViewViews,
    ManageViews,
    EditViews,
    ViewNetworks,
    ManageNetworks,

    /// <summary>
    /// System-wide authority to delete any ISO in any View/team, including ones the caller is not a
    /// member of. Used by the "all views" ISO management mode.
    /// </summary>
    DeleteIsos
}

public enum AppViewPermission
{
    ViewView,
    ManageView,
    EditView,

    /// <summary>Upload ISOs view-wide (public) and to any team in the View.</summary>
    UploadViewIsos,

    /// <summary>Delete view-wide (public) ISOs and any team's ISOs in the View.</summary>
    DeleteViewIsos,
    DownloadVmFiles,
    UploadVmFiles,
    RevertVms,
    ViewNetworks,
    ManageNetworks
}

public enum AppTeamPermission
{
    ViewTeam,
    ManageTeam,
    EditTeam,

    /// <summary>Upload ISOs to this specific team.</summary>
    UploadTeamIsos,

    /// <summary>Delete ISOs belonging to this specific team.</summary>
    DeleteTeamIsos
}