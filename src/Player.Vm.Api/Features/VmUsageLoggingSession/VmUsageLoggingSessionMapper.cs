// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using Riok.Mapperly.Abstractions;

namespace Player.Vm.Api.Features.VmUsageLoggingSession;

/// <summary>Vm usage logging sessions' mappings.</summary>
[Mapper]
public static partial class VmUsageLoggingSessionMapper
{
    // ---- Entity to view model -------------------------------------------------------------------

    /// <remarks>Projected as stored: a session stored without team ids has null team ids here.</remarks>
    public static partial IQueryable<VmUsageLoggingSession> ProjectToVmUsageLoggingSession(this IQueryable<Domain.Models.VmUsageLoggingSession> query);

    // The projection's element mapping: team ids as stored.
    [UserMapping(Default = true)]
    private static partial VmUsageLoggingSession MapToVmUsageLoggingSession(Domain.Models.VmUsageLoggingSession source);

    /// <remarks>Unlike the projection, a session stored without team ids has an empty list here.</remarks>
    [UserMapping(Default = false)]
    [MapProperty(nameof(Domain.Models.VmUsageLoggingSession.TeamIds), nameof(VmUsageLoggingSession.TeamIds), Use = nameof(CopyTeamIds))]
    public static partial VmUsageLoggingSession ToVmUsageLoggingSession(this Domain.Models.VmUsageLoggingSession source);

    public static partial IQueryable<VmUsageLogEntry> ProjectToVmUsageLogEntry(this IQueryable<Domain.Models.VmUsageLogEntry> query);

    // ---- Command to entity ----------------------------------------------------------------------

    /// <remarks>The handler stamps the creation time.</remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapProperty(nameof(Create.Command.ViewId), nameof(Domain.Models.VmUsageLoggingSession.ViewId), Use = nameof(ViewIdOrEmpty))]
    [MapProperty(nameof(Create.Command.TeamIds), nameof(Domain.Models.VmUsageLoggingSession.TeamIds), Use = nameof(CopyTeamIds))]
    public static partial Domain.Models.VmUsageLoggingSession ToEntity(this Create.Command source);

    /// <remarks>
    /// The id is the route's and names the session being edited. A command without a view id writes an
    /// empty one, moving the session out of its View, and one without team ids clears them.
    /// </remarks>
    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    [MapperIgnoreSource(nameof(Edit.Command.Id))]
    [MapProperty(nameof(Edit.Command.ViewId), nameof(Domain.Models.VmUsageLoggingSession.ViewId), Use = nameof(ViewIdOrEmpty))]
    [MapProperty(nameof(Edit.Command.TeamIds), nameof(Domain.Models.VmUsageLoggingSession.TeamIds), Use = nameof(CopyTeamIds))]
    public static partial void ApplyTo(this Edit.Command source, Domain.Models.VmUsageLoggingSession target);

    private static Guid ViewIdOrEmpty(Guid? viewId) => viewId ?? Guid.Empty;

    [UserMapping(Default = false)]
    private static Guid[] CopyTeamIds(Guid[] source) => source == null ? [] : [.. source];
}
