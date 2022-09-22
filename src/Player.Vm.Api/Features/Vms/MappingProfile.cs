// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using Player.Vm.Api.Infrastructure.Options;
using System;
using System.Linq;

namespace Player.Vm.Api.Features.Vms
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            CreateMap<Domain.Models.Vm, Vm>()
                .ForMember(dest => dest.TeamIds, opt => opt.MapFrom(src => src.VmTeams.Select(x => x.TeamId)))
                .ForMember(dest => dest.Url, opt => opt.MapFrom<ConsoleUrlResolver>());

            CreateMap<Domain.Models.ConsoleConnectionInfo, ConsoleConnectionInfo>().ReverseMap();

            CreateMap<VmUpdateForm, Domain.Models.Vm>();

            CreateMap<VmCreateForm, Domain.Models.Vm>()
                .ForMember(dest => dest.Type, opt => opt.MapFrom(src => src.ProxmoxVmInfo != null ? Domain.Models.VmType.Proxmox : Domain.Models.VmType.Unknown))
                .ForMember(dest => dest.VmTeams, opt => opt.MapFrom(src => src.TeamIds.Select(x => new Domain.Models.VmTeam(x, src.Id.Value))));

            CreateMap<VmMap, Domain.Models.VmMap>();

            CreateMap<Domain.Models.VmMap, VmMap>();

            CreateMap<VmMapCreateForm, Domain.Models.VmMap>()
                .ForMember(dest => dest.Coordinates, opt => opt.Ignore());

            CreateMap<CoordinateCreateForm, Domain.Models.Coordinate>();

            CreateMap<Coordinate, Domain.Models.Coordinate>();

            CreateMap<Domain.Models.Coordinate, Coordinate>();

            CreateMap<VmMapCreateForm, VmMap>();

            CreateMap<VmMap, VmMapCreateForm>();

            CreateMap<CoordinateCreateForm, Coordinate>();

            CreateMap<Coordinate, CoordinateCreateForm>();

            CreateMap<VmMapUpdateForm, Domain.Models.VmMap>()
                .ForMember(dest => dest.Coordinates, opt => opt.Ignore());

            CreateMap<Domain.Models.ProxmoxVmInfo, ProxmoxVmInfo>().ReverseMap();
        }
    }

    public class ConsoleUrlResolver : IValueResolver<Domain.Models.Vm, Vm, string>
    {
        private readonly ConsoleUrlOptions _consoleUrlOptions;

        public ConsoleUrlResolver(ConsoleUrlOptions consoleUrlOptions)
        {
            _consoleUrlOptions = consoleUrlOptions;
        }

        public string Resolve(Domain.Models.Vm source, Vm destination, string member, ResolutionContext context)
        {
            return source.GetUrl(_consoleUrlOptions);
        }
    }
}
