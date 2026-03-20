// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;

namespace Player.Vm.Api.Features.Networks
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            CreateMap<Domain.Models.ViewNetwork, ViewNetwork>();
            CreateMap<CreateViewNetworkForm, Domain.Models.ViewNetwork>();
            CreateMap<UpdateViewNetworkForm, Domain.Models.ViewNetwork>();
        }
    }
}
