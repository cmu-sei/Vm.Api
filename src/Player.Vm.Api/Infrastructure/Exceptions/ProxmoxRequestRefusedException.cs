// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;

namespace Player.Vm.Api.Infrastructure.Exceptions
{
    /// <summary>
    /// Proxmox refused an operation outright rather than queueing a task for it - the Vm is locked by a
    /// backup or another task, a config file could not be locked, the snapshot does not exist. Reported
    /// with Proxmox's own reason as the title: the API worked, the hypervisor said no, and the reason is
    /// what the user needs to act on.
    /// </summary>
    /// <remarks>
    /// A 409 when Proxmox answered with its own HTTP 500, which is how PVE's API handlers report a
    /// request the Vm's current state rules out - locked, already running, no such snapshot. Anything
    /// else Proxmox refused - a 400 "Parameter verification failed", a token it would not accept, a node
    /// it could not reach - stays a 400, as it was before this API had a 409.
    /// </remarks>
    public class ProxmoxRequestRefusedException : Exception, IApiException
    {
        public string Reason { get; }
        public string Operation { get; }

        /// <summary>The HTTP status Proxmox answered with, or null when it is not known.</summary>
        public HttpStatusCode? ProxmoxStatus { get; }

        public ProxmoxRequestRefusedException(string reason, string operation = null, HttpStatusCode? proxmoxStatus = null)
            : base(Describe(reason, operation))
        {
            Reason = reason;
            Operation = operation;
            ProxmoxStatus = proxmoxStatus;
        }

        public HttpStatusCode GetStatusCode() =>
            ProxmoxStatus == HttpStatusCode.InternalServerError ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest;

        private static string Describe(string reason, string operation)
        {
            var text = string.IsNullOrWhiteSpace(reason) ? "unknown error" : reason.Trim();

            return string.IsNullOrWhiteSpace(operation)
                ? $"Proxmox refused the request: {text}"
                : $"{operation} failed: {text}";
        }
    }
}
