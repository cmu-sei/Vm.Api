/*
Copyright 2021 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;


namespace Player.Vm.Api.Domain.Services.HealthChecks
{
    public class ConnectionServiceHealthCheck : IHealthCheck
    {
        private int _healthAllowance = 90;
        private DateTime _lastRun = DateTime.Now;

        public int HealthAllowance
        {
            get => _healthAllowance;
            set => _healthAllowance = value;
        }
        public DateTime LastRun
        {
            get => _lastRun;
            set => _lastRun = value;
        }
        public void CompletedRun()
        {
            LastRun = DateTime.Now;
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if ((DateTime.Now - LastRun).TotalSeconds < HealthAllowance)
            {
                return Task.FromResult(
                    HealthCheckResult.Healthy("The Connection Service is responsive."));
            }

            return Task.FromResult(
                HealthCheckResult.Unhealthy("The Connection Service is not responsive."));
        }
    }
} 