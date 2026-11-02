// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Player.Vm.Api.Domain.Models;

using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Domain.Proxmox.Extensions
{
    public static class ProxmoxExtensions
    {
        private const int DefaultTaskPollMs = 2000;
        private const long DefaultTaskTimeoutMs = 3600 * 1000;

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
        /// What Proxmox said about a failed call, never empty.
        /// </summary>
        /// <remarks>
        /// <c>Result.GetError()</c> reads only an <c>errors</c> object, which PVE sends for parameter
        /// validation. Most refusals - no guest agent, a missing snapshot, a rejected token, a gateway in
        /// front of PVE - carry a <c>message</c> instead, or no body at all, and <c>GetError()</c> is
        /// empty for every one of them. So this falls back from <c>errors</c> to <c>message</c> to the
        /// HTTP status line.
        /// </remarks>
        public static string GetErrorMessage(this Result result)
        {
            var error = result.GetError();

            if (!string.IsNullOrWhiteSpace(error))
                return error;

            var message = GetField(result.Response, "message")?.ToString();

            if (!string.IsNullOrWhiteSpace(message))
                return message.Trim();

            var status = $"HTTP {(int)result.StatusCode}";

            return string.IsNullOrWhiteSpace(result.ReasonPhrase) ? status : $"{status} {result.ReasonPhrase.Trim()}";
        }

        /// <summary>
        /// One field of an object in a Proxmox response, or null when the response leaves it out.
        /// </summary>
        /// <remarks>
        /// The client hands response objects out as <c>ExpandoObject</c>s, and a dynamic read of a key
        /// an <c>ExpandoObject</c> does not contain throws rather than answering null - so a field PVE
        /// omits has to be looked up through the <c>IDictionary</c> an <c>ExpandoObject</c> also is.
        /// </remarks>
        public static object GetField(object data, string field) =>
            data is IDictionary<string, object> dict && dict.TryGetValue(field, out var value) ? value : null;

        /// <summary>
        /// Waits for the task a submitted operation started, turning anything short of a clean finish
        /// into an exception.
        /// </summary>
        /// <remarks>
        /// Here rather than on a service because both <see cref="Services.IProxmoxService"/> (Vm
        /// lifecycle) and <see cref="Services.IProxmoxIsoStorageService"/> (ISO storage) submit
        /// operations that return a task, and neither owns the polling.
        /// </remarks>
        public static async Task WaitAndThrow(
            this PveClient client,
            Result result,
            string operation,
            CancellationToken cancellationToken = default,
            int wait = DefaultTaskPollMs,
            long timeout = DefaultTaskTimeoutMs)
        {
            // A refusal is a 400 with Proxmox's reason, not a 500: PVE answers a locked Vm, an unlockable
            // config file or a missing snapshot with an HTTP 500 of its own, but that is the hypervisor
            // declining the request, not this API failing. The node-migration retry happens before this,
            // on the Result, so it is unaffected by what is thrown here.
            if (!result.IsSuccessStatusCode)
                throw new ProxmoxRequestRefusedException(result.GetErrorMessage(), OperationName(operation));

            bool finished;
            try
            {
                finished = await WaitForTaskToFinish(client, result, wait, timeout, cancellationToken);
            }
            catch (ProxmoxTaskFailedException ex) when (ex.Operation == null)
            {
                throw new ProxmoxTaskFailedException(ex.Status, OperationName(operation));
            }
            catch (ProxmoxRequestRefusedException ex) when (ex.Operation == null)
            {
                throw new ProxmoxRequestRefusedException(ex.Reason, OperationName(operation));
            }

            // A 504 rather than a 500: Proxmox took the request and has not finished it, which is an
            // upstream that did not answer in time, not a fault here.
            if (!finished)
                throw new ProxmoxTaskTimeoutException(OperationName(operation), TimeSpan.FromMilliseconds(timeout));
        }

        // The callers describe an operation as e.g. "RevertToCurrentSnapshot vmid=100"; a user is told
        // about it by the name the UI gives the action, without the identifiers.
        private static readonly IReadOnlyDictionary<string, string> OperationNames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PowerOn"] = "Power On",
                ["PowerOff"] = "Power Off",
                ["Reboot"] = "Reboot",
                ["Shutdown"] = "Shutdown",
                ["CreateSnapshot"] = "Create Snapshot",
                ["RevertSnapshot"] = "Revert to Snapshot",
                ["RevertToCurrentSnapshot"] = "Revert",
                ["DeleteSnapshot"] = "Delete Snapshot",
                ["MountIso"] = "Mount ISO",
                ["ChangeNetwork"] = "Change Network",
                ["UploadIso"] = "Upload ISO",
                ["DeleteIso"] = "Delete ISO",
            };

        internal static string OperationName(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation))
                return null;

            var verb = operation.Split(' ', 2)[0];

            return OperationNames.TryGetValue(verb, out var name) ? name : verb;
        }

        /// <summary>
        /// Waits for a Proxmox task to finish and checks its final exit status.
        /// </summary>
        public static async Task<bool> WaitForTaskToFinish(
            this PveClient client,
            Result result,
            int wait = DefaultTaskPollMs,
            long timeout = DefaultTaskTimeoutMs,
            CancellationToken cancellationToken = default)
        {
            if (result == null || timeout <= 0)
                return false;

            if (result.ResponseInError || !result.IsSuccessStatusCode)
                throw new ProxmoxRequestRefusedException(result.GetErrorMessage());

            var data = result.ToData() as string;
            if (data is null || !data.StartsWith("UPID:"))
                return true;

            var finished = await WaitForTaskToFinish(client, data, wait, timeout, cancellationToken);

            if (finished)
            {
                var status = await client.GetExitStatusTaskAsync(data);

                // "WARNINGS: n" is how PVE reports a task that succeeded with warnings.
                if (status != "OK" && !(status?.StartsWith("WARNINGS", StringComparison.Ordinal) ?? false))
                    throw new ProxmoxTaskFailedException(status);
            }

            return finished;
        }

        /// <summary>
        /// Waits for a Proxmox task to stop running.
        /// </summary>
        public static async Task<bool> WaitForTaskToFinish(
            this PveClient client,
            string task,
            int wait = DefaultTaskPollMs,
            long timeout = DefaultTaskTimeoutMs,
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
