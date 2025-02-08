// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Player.Vm.Api.Infrastructure.Authorization;

namespace Player.Vm.Api.Domain.Models;
public enum Permissions
{
    ReadOnly,
    ViewAdmin,
    SystemAdmin
}

public class VmPermissionResult
{
    public AppViewPermission[] ViewPermissions { get; set; }
    public AppTeamPermission[] TeamPermissions { get; set; }
}