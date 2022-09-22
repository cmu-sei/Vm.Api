/*
Copyright 2022 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Threading;
using Microsoft.AspNetCore.Authorization;

namespace Player.Vm.Api.Features.HealthCheck
{
    [Route("api/")]
    [ApiController]
    [AllowAnonymous]
    public class HealthController : ControllerBase
    {
        private readonly HealthCheckService healthCheckService;

        public HealthController(HealthCheckService healthCheckService)
        {
            this.healthCheckService = healthCheckService;
        }

        /// <summary>
        /// Checks the liveliness health endpoint
        /// </summary>
        /// <remarks>
        /// Returns a HealthStatus of the liveliness health check
        /// </remarks>
        /// <returns></returns>
        [HttpGet("health/live")]
        [ProducesResponseType(typeof(HealthStatus), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "Health_GetLiveliness")]
        public async Task<IActionResult> GetLiveliness(CancellationToken ct)
        {
            HealthReport report = await this.healthCheckService.CheckHealthAsync((check) => check.Tags.Contains("live"));
            return report.Status == HealthStatus.Healthy ? this.Ok(report.Status) : this.StatusCode((int)HttpStatusCode.ServiceUnavailable, report.Status);
        }

        /// <summary>
        /// Checks the readiness health endpoint
        /// </summary>
        /// <remarks>
        /// Returns a HealthStatus of the readiness health check
        /// </remarks>
        /// <returns></returns>
        [HttpGet("health/ready")]
        [ProducesResponseType(typeof(HealthStatus), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "Health_GetReadiness")]
        public async Task<IActionResult> GetReadiness(CancellationToken ct)
        {
            HealthReport report = await this.healthCheckService.CheckHealthAsync((check) => check.Tags.Contains("ready"));
            return report.Status == HealthStatus.Healthy ? this.Ok(report.Status) : this.StatusCode((int)HttpStatusCode.ServiceUnavailable, report.Status);
        }
    }
}