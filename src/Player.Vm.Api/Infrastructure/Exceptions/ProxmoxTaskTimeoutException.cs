// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Globalization;
using System.Net;

namespace Player.Vm.Api.Infrastructure.Exceptions
{
    /// <summary>
    /// A Proxmox task this API gave up waiting for. Reported as a 504 Gateway Timeout: Proxmox accepted
    /// the request and is the one that has not answered, which is neither the caller's mistake (4xx) nor
    /// a fault in this API (500). The task is not cancelled, so it may still finish.
    /// </summary>
    /// <remarks>
    /// A <see cref="TimeoutException"/> still, so anything that already treats a timeout as one keeps
    /// doing so.
    /// </remarks>
    public class ProxmoxTaskTimeoutException : TimeoutException, IApiException
    {
        public string Operation { get; }
        public TimeSpan Timeout { get; }

        public ProxmoxTaskTimeoutException(string operation, TimeSpan timeout)
            : base(Describe(operation, timeout))
        {
            Operation = operation;
            Timeout = timeout;
        }

        public HttpStatusCode GetStatusCode() => HttpStatusCode.GatewayTimeout;

        private static string Describe(string operation, TimeSpan timeout)
        {
            var subject = string.IsNullOrWhiteSpace(operation) ? "The Proxmox task" : operation;

            return $"{subject} did not finish within {Format(timeout)}. The Proxmox task may still be running; check the virtual machine before trying again.";
        }

        private static string Format(TimeSpan timeout)
        {
            if (timeout.TotalMinutes >= 1 && timeout.TotalMinutes == Math.Floor(timeout.TotalMinutes))
            {
                var minutes = (long)timeout.TotalMinutes;
                return minutes == 1 ? "1 minute" : $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes";
            }

            var seconds = Math.Max(1, (long)Math.Ceiling(timeout.TotalSeconds));
            return seconds == 1 ? "1 second" : $"{seconds.ToString(CultureInfo.InvariantCulture)} seconds";
        }
    }
}
