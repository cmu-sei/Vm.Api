// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.AspNetCore.Builder;

namespace Player.Vm.Api.Infrastructure.Exceptions.Middleware
{
    public static class ExceptionMiddlewareExtensions
    {
        public static void UseCustomExceptionHandler(this IApplicationBuilder app)
        {
            app.UseMiddleware<ExceptionMiddleware>();
        }
    }
}
