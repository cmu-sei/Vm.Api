// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;

namespace Player.Vm.Api.Infrastructure.Exceptions
{
    /// <summary>
    /// Proxmox refused an operation outright rather than queueing a task for it - the Vm is locked by a
    /// backup or another task, a config file could not be locked, the snapshot does not exist. Reported
    /// as a 400 carrying Proxmox's own reason, the same as <see cref="ProxmoxTaskFailedException"/>: the
    /// API worked, the hypervisor said no, and the reason is what the user needs to act on.
    /// </summary>
    public class ProxmoxRequestRefusedException : Exception, IApiException
    {
        public string Reason { get; }
        public string Operation { get; }

        public ProxmoxRequestRefusedException(string reason, string operation = null)
            : base(Describe(reason, operation))
        {
            Reason = reason;
            Operation = operation;
        }

        public HttpStatusCode GetStatusCode() => HttpStatusCode.BadRequest;

        private static string Describe(string reason, string operation)
        {
            var text = string.IsNullOrWhiteSpace(reason) ? "unknown error" : reason.Trim();

            return string.IsNullOrWhiteSpace(operation)
                ? $"Proxmox refused the request: {text}"
                : $"{operation} failed: {text}";
        }
    }
}
