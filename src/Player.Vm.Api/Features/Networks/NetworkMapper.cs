// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Riok.Mapperly.Abstractions;

namespace Player.Vm.Api.Features.Networks;

/// <summary>View networks' mappings. Missing team ids are an empty list, never null.</summary>
[Mapper]
public static partial class NetworkMapper
{
    public static partial ViewNetwork ToViewNetwork(this Domain.Models.ViewNetwork source);

    /// <remarks>The handler sets the View.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial Domain.Models.ViewNetwork ToEntity(this CreateViewNetworkForm source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this UpdateViewNetworkForm source, Domain.Models.ViewNetwork target);

    private static Guid[] CopyTeamIds(Guid[] source) => source == null ? [] : [.. source];
}
