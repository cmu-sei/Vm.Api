// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Features.Shared.Interfaces
{
    /// <summary>
    /// Marks a request that should kick the vSphere task poller once it completes. The Proxmox
    /// counterpart is <see cref="ICheckProxmoxTasksRequest"/>; a request that can target either
    /// provider implements both.
    /// </summary>
    public interface ICheckVsphereTasksRequest { }
}
