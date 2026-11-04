// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;

namespace Player.Vm.Api.Infrastructure.Exceptions
{
    /// <summary>
    /// A Proxmox task that ran and finished with a failure status, such as a reboot the guest never
    /// answered. Reported as a 400 carrying Proxmox's own reason rather than as a server error: the
    /// API worked, the hypervisor or guest refused.
    /// </summary>
    public class ProxmoxTaskFailedException : Exception, IApiException
    {
        public string Status { get; }
        public string Operation { get; }

        public ProxmoxTaskFailedException(string status, string operation = null)
            : base(Describe(status, operation))
        {
            Status = status;
            Operation = operation;
        }

        public HttpStatusCode GetStatusCode() => HttpStatusCode.BadRequest;

        private static string Describe(string status, string operation)
        {
            var reason = string.IsNullOrWhiteSpace(status) ? "unknown error" : status.Trim();
            var message = string.IsNullOrWhiteSpace(operation)
                ? $"The Proxmox task failed: {reason}"
                : $"{operation} failed: {reason}";

            // PVE's graceful reboot and shutdown ask the guest through ACPI (or the guest agent) and give
            // up with this status when nothing in the guest answers.
            if (reason.Contains("quit/powerdown failed", StringComparison.OrdinalIgnoreCase))
            {
                message += ". The guest did not respond to the request; use Power Off instead, or install acpid or the QEMU guest agent in the guest.";
            }

            return message;
        }
    }
}
