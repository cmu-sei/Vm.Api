// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Riok.Mapperly.Abstractions;

// Defaults for every Mapperly [Mapper] in the application. .editorconfig raises unmapped members and
// projections that cannot be inlined to errors, so a member that stops being mapped fails the build.
[assembly: MapperDefaults(
    // Outbound maps (entity to view model) must fill every member of the view model, so a response
    // never silently carries a default. Inbound maps (request onto entity) ask the opposite question
    // instead, with [MapperRequiredMapping(RequiredMappingStrategy.Source)]: every member of the
    // request must reach the entity, while navigations and members the request does not carry are
    // left alone.
    RequiredMappingStrategy = RequiredMappingStrategy.Target,

    // A null source value is skipped rather than written when the target cannot hold null. False is
    // Mapperly's default, stated here so it cannot change unnoticed. AutoMapper, configured here
    // without conventions, wrote the target type's default instead; the one request where that
    // differs (a usage session edit without a view id) maps the member explicitly.
    ThrowOnPropertyMappingNullMismatch = false)]
