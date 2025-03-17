// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Authorization;

public enum AppSystemPermission
{
    ViewViews,
    ManageViews,
    EditViews
}

public enum AppViewPermission
{
    ViewView,
    ManageView,
    EditView,
    UploadViewIsos,
    DownloadVmFiles,
    UploadVmFiles,
    RevertVms
}

public enum AppTeamPermission
{
    ViewTeam,
    ManageTeam,
    EditTeam,
    UploadTeamIsos
}