// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Authorization;

public enum AppSystemPermission
{
    ViewViews,
    ManageViews,
    ViewNetworks,
    ManageNetworks,

    /// <summary>
    /// System-wide authority to delete any ISO in any View/team, including ones the caller is not a
    /// member of. Used by the "all views" ISO management mode.
    /// </summary>
    DeleteIsos,

    /// <summary>View every Vm console in the system without being able to interact with them.</summary>
    ViewVms,

    /// <summary>Interact with and control every Vm in the system.</summary>
    ControlVms,

    /// <summary>View every Map in the system without being able to change them.</summary>
    ViewMaps,

    /// <summary>View, create, edit, and delete every Map in the system.</summary>
    ManageMaps
}

public enum AppViewPermission
{
    ViewView,
    ManageView,

    /// <summary>Upload ISOs view-wide (public) and to any team in the View.</summary>
    UploadViewIsos,

    /// <summary>Delete view-wide (public) ISOs and any team's ISOs in the View.</summary>
    DeleteViewIsos,
    DownloadVmFiles,
    UploadVmFiles,
    RevertVms,
    ViewNetworks,
    ManageNetworks,

    /// <summary>View every Vm console in the View without being able to interact with them.</summary>
    ViewViewVms,

    /// <summary>Interact with and control every Vm in the View.</summary>
    ControlViewVms,

    /// <summary>View every Map in the View without being able to change them.</summary>
    ViewViewMaps,

    /// <summary>View, create, edit, and delete every Map in the View.</summary>
    ManageViewMaps
}

public enum AppTeamPermission
{
    ViewTeam,
    ManageTeam,

    /// <summary>Upload ISOs to this specific team.</summary>
    UploadTeamIsos,

    /// <summary>Delete ISOs belonging to this specific team.</summary>
    DeleteTeamIsos,

    /// <summary>View the team's Vm consoles without being able to interact with them.</summary>
    ViewTeamVms,

    /// <summary>Interact with and control the team's Vms.</summary>
    ControlTeamVms,

    /// <summary>View the team's Maps without being able to change them.</summary>
    ViewTeamMaps,

    /// <summary>View, create, edit, and delete the team's Maps.</summary>
    ManageTeamMaps
}
