// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Player.Vm.Api.Domain.Models;

namespace Player.Vm.Api.Domain.Proxmox.Extensions
{
    public static class ProxmoxExtensions
    {
        /// <summary>
        /// Gets a generic PowerState from a Proxmox virtual machine
        /// </summary>
        public static PowerState GetPowerState(this IClusterResourceVm vm)
        {
            if (vm == null)
                return PowerState.Unknown;

            if (vm.IsRunning)
                return PowerState.On;

            if (vm.IsStopped)
                return PowerState.Off;

            if (vm.IsPaused)
                return PowerState.Suspended;

            return PowerState.Unknown;
        }

        /// <summary>
        /// Waits for a Proxmox task to finish and checks its final exit status.
        /// </summary>
        public static async Task<bool> WaitForTaskToFinish(
            this PveClient client,
            Result result,
            int wait = 2000,
            long timeout = 3600 * 1000,
            CancellationToken cancellationToken = default)
        {
            if (result == null || timeout <= 0)
                return false;

            if (result.ResponseInError || !result.IsSuccessStatusCode)
            {
                var statusStr = $"\n Status Code: {result.StatusCode}";
                var reasonStr = string.IsNullOrEmpty(result.ReasonPhrase) ? "" : $"\n Reason: {result.ReasonPhrase}";
                var error = result.GetError();
                var errorStr = string.IsNullOrEmpty(error) ? "" : $"\n Error: {error}";

                throw new Exception($"Task failed: {statusStr}{reasonStr}{errorStr}");
            }

            var data = result.ToData() as string;
            if (data is null || !data.StartsWith("UPID:"))
                return true;

            var finished = await WaitForTaskToFinish(client, data, wait, timeout, cancellationToken);

            if (finished)
            {
                var status = await client.GetExitStatusTaskAsync(data);

                if (status != "OK")
                    throw new Exception($"Task failed: {status}");
            }

            return finished;
        }

        /// <summary>
        /// Waits for a Proxmox task to stop running.
        /// </summary>
        public static async Task<bool> WaitForTaskToFinish(
            this PveClient client,
            string task,
            int wait = 2000,
            long timeout = 3600 * 1000,
            CancellationToken cancellationToken = default)
        {
            var isRunning = true;
            if (wait <= 0)
                wait = 500;
            if (timeout < wait)
                timeout = wait + 5000;

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            while (isRunning && stopwatch.ElapsedMilliseconds < timeout)
            {
                await Task.Delay(wait, cancellationToken);
                isRunning = await client.TaskIsRunningAsync(task);
            }

            stopwatch.Stop();

            return stopwatch.ElapsedMilliseconds < timeout;
        }
    }
}
