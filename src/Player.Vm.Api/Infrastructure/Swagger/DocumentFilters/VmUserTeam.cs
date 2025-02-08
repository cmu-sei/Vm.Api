// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.OpenApi.Models;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Authorization;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Player.Vm.Api.Infrastructure.OperationFilters
{
    public class ModelDocumentFilter : IDocumentFilter
    {
        public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        {

            context.SchemaGenerator.GenerateSchema(typeof(VmUserTeam), context.SchemaRepository);
            context.SchemaGenerator.GenerateSchema(typeof(VmUser), context.SchemaRepository);
            context.SchemaGenerator.GenerateSchema(typeof(AppSystemPermission), context.SchemaRepository);
            context.SchemaGenerator.GenerateSchema(typeof(AppViewPermission), context.SchemaRepository);
            context.SchemaGenerator.GenerateSchema(typeof(AppTeamPermission), context.SchemaRepository);
        }
    }
}
