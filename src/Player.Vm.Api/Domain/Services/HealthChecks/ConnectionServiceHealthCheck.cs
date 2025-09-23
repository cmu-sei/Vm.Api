/*
Copyright 2022 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Player.Vm.Api.Domain.Vsphere.Models;


namespace Player.Vm.Api.Domain.Services.HealthChecks
{
    public class ConnectionServiceHealthCheck : IHealthCheck
    {
        public VsphereConnection[] Connections { get; set; }
        public bool StartupCheckComplete { get; set; }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!StartupCheckComplete)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("Initial connections attempts have not yet been completed."));
            }

            if (Connections.Any(x => x.Enabled && !x.Connected))
            {
                return Task.FromResult(HealthCheckResult.Degraded("One or more enabled hosts are not connected."));
            }

            return Task.FromResult(HealthCheckResult.Healthy());
        }
    }
}