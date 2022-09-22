// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;

namespace Player.Vm.Api.Features.Proxmox
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            CreateMap<Domain.Proxmox.Models.ProxmoxConsole, GetConsole.ProxmoxConsole>();
        }
    }
}
