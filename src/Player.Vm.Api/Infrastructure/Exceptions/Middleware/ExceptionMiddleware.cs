// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Player.Vm.Api.Infrastructure.Serialization;

namespace Player.Vm.Api.Infrastructure.Exceptions.Middleware
{
    public class ExceptionMiddleware
    {
        private readonly IWebHostEnvironment _env;
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;

        public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger, IWebHostEnvironment env)
        {
            _logger = logger;
            _next = next;
            _env = env;
        }

        public async Task InvokeAsync(HttpContext httpContext)
        {
            try
            {
                await _next(httpContext);
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(httpContext, ex);
            }
        }

        private async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            int statusCode = GetStatusCodeFromException(exception);

            var error = new ProblemDetails();
            error.Status = statusCode;

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";

            if (statusCode == (int)HttpStatusCode.InternalServerError)
            {
                if (_env.IsDevelopment())
                {
                    error.Title = exception.Message;
                    error.Detail = exception.ToString();
                }
                else
                {
                    error.Title = "A server error occurred.";
                    error.Detail = exception.Message;
                }
            }
            else
            {
                error.Title = exception.Message;
            }

            await context.Response.WriteAsync(JsonSerializer.Serialize(error, DefaultJsonSettings.Settings));
        }

        /// <summary>
        /// map all custom exceptions to proper http status code
        /// </summary>
        /// <returns></returns>
        private int GetStatusCodeFromException(Exception exception)
        {
            HttpStatusCode statusCode = HttpStatusCode.InternalServerError;

            if (exception is IApiException)
            {
                statusCode = (exception as IApiException).GetStatusCode();
                _logger.LogDebug($"API Exception: {exception}");
            }
            else
            {
                _logger.LogError($"Unhandled Exception: {exception}");
            }

            return (int)statusCode;
        }
    }
}
