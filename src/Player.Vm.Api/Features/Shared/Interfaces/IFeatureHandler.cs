// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Features.Shared.Interfaces
{
    // Empty marker interface for per-endpoint request handler classes. Implementers are discovered
    // by a reflection scan (AddFeatureHandlers) and registered as Scoped, so a new handler needs only
    // to implement this marker - no manual DI registration. Named to avoid colliding with the
    // heavily-used MediatR.IRequestHandler<,>.
    public interface IFeatureHandler { }
}
