// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Infrastructure.Options;
using Riok.Mapperly.Abstractions;

namespace Player.Vm.Api.Features.Vms;

/// <summary>The Vms feature's mappings: Vms and their maps.</summary>
/// <remarks>Missing lists (ip addresses, team ids, coordinates, urls) are empty lists in the result, never null.</remarks>
[Mapper]
public static partial class VmMapper
{
    // ---- Vm: entity to view model ---------------------------------------------------------------

    /// <summary>The view model of a Vm, with the console url it is opened at under <paramref name="consoleUrls"/>.</summary>
    public static Vm ToVm(this Domain.Models.Vm source, ConsoleUrlOptions consoleUrls)
    {
        var vm = MapToVm(source);

        if (vm != null)
            vm.Url = source.GetUrl(consoleUrls);

        return vm;
    }

    [MapProperty(nameof(Domain.Models.Vm.VmTeams), nameof(Vm.TeamIds), Use = nameof(MapTeamIds))]
    [MapperIgnoreTarget(nameof(Vm.Url))]
    private static partial Vm MapToVm(Domain.Models.Vm source);

    private static IEnumerable<Guid> MapTeamIds(ICollection<VmTeam> vmTeams) =>
        vmTeams == null ? [] : vmTeams.Select(x => x.TeamId).ToList();

    // ---- Vm: request to entity ------------------------------------------------------------------

    /// <remarks>A Vm created with Proxmox details is a Proxmox Vm; any other is of unknown type until its poller sets it.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(VmCreateForm.TeamIds))]
    [MapPropertyFromSource(nameof(Domain.Models.Vm.Type), Use = nameof(CreatedType))]
    [MapPropertyFromSource(nameof(Domain.Models.Vm.VmTeams), Use = nameof(CreatedVmTeams))]
    public static partial Domain.Models.Vm ToEntity(this VmCreateForm source);

    private static VmType CreatedType(VmCreateForm source) =>
        source.ProxmoxVmInfo != null ? VmType.Proxmox : VmType.Unknown;

    // Throws when there are teams but no id, answered with a 500.
    private static ICollection<VmTeam> CreatedVmTeams(VmCreateForm source) =>
        source.TeamIds == null ? new List<VmTeam>() : source.TeamIds.Select(x => new VmTeam(x, source.Id.Value)).ToList();

    /// <summary>Applies an update to a stored Vm.</summary>
    /// <remarks>
    /// Keeps <see cref="Domain.Models.Vm.Type"/> in step with the Proxmox details, but never clobbers the
    /// type of a Vm from another provider (vSphere's poller sets its own). The Proxmox details themselves
    /// are left to <c>VmService.UpdateAsync</c>: their vmid is the row's key, which EF cannot change in place.
    /// A null form changes nothing.
    /// </remarks>
    public static void ApplyTo(this VmUpdateForm source, Domain.Models.Vm target)
    {
        if (source == null || target == null)
            return;

        target.Type = source.ProxmoxVmInfo != null
            ? VmType.Proxmox
            : target.Type == VmType.Proxmox
                ? VmType.Unknown
                : target.Type;

        MapOnto(source, target);
    }

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(VmUpdateForm.ProxmoxVmInfo))]
    private static partial void MapOnto(VmUpdateForm source, Domain.Models.Vm target);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    private static partial Domain.Models.ProxmoxVmInfo MapToEntity(ProxmoxVmInfo source);

    // ---- Maps -----------------------------------------------------------------------------------

    public static partial VmMap ToVmMap(this Domain.Models.VmMap source);

    /// <remarks>The handler sets the View; the coordinates keep the ids the request gives them.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial Domain.Models.VmMap ToEntity(this VmMapCreateForm source);

    /// <remarks>The handler replaces the coordinates itself.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(VmMapUpdateForm.Coordinates))]
    public static partial void ApplyTo(this VmMapUpdateForm source, Domain.Models.VmMap target);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial Domain.Models.Coordinate ToEntity(this CoordinateCreateForm source);

    private static IEnumerable<Coordinate> MapCoordinates(List<Domain.Models.Coordinate> source) =>
        source == null ? [] : source.Select(x => MapToCoordinate(x)).ToList();

    private static partial Coordinate MapToCoordinate(Domain.Models.Coordinate source);

    private static List<Domain.Models.Coordinate> MapCoordinates(IEnumerable<CoordinateCreateForm> source) =>
        source == null ? [] : source.Select(x => x.ToEntity()).ToList();

    // ---- Lists ----------------------------------------------------------------------------------

    private static string[] CopyStrings(string[] source) => source == null ? [] : [.. source];

    private static List<Guid> CopyGuids(List<Guid> source) => source == null ? [] : [.. source];
}
