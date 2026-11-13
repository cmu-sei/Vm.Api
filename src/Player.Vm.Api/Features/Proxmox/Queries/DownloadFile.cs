// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Player.Vm.Api.Data;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Domain.Services;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Infrastructure.Authorization;
using Player.Vm.Api.Infrastructure.Exceptions;

namespace Player.Vm.Api.Features.Proxmox.Queries;

/// <summary>
/// Downloads a file out of a Proxmox Vm's guest as raw bytes, through the QEMU guest agent. Gated exactly
/// as read-guest-file is.
/// </summary>
public class DownloadFile
{
    [DataContract(Name = "DownloadFileFromProxmoxVirtualMachineQuery")]
    public class Query : IRequest<FileResult>
    {
        public Guid Id { get; set; }

        /// <summary>The file's path in the guest.</summary>
        public string Path { get; set; }
    }

    public class Handler : BaseHandler, IRequestHandler<Query, FileResult>
    {
        private const string ContentType = "application/octet-stream";

        private readonly IProxmoxService _proxmoxService;

        public Handler(VmContext db, IPlayerService playerService, IVmService vmService, IProxmoxService proxmoxService)
            : base(db, playerService, vmService)
        {
            _proxmoxService = proxmoxService;
        }

        public async Task<FileResult> Handle(Query request, CancellationToken cancellationToken)
        {
            var vm = await GetVm(request.Id, [AppSystemPermission.DownloadVmFiles], [AppViewPermission.DownloadVmFiles], [], cancellationToken,
                "You do not have permission to download files from this vm.");

            if (string.IsNullOrWhiteSpace(request.Path))
                throw new BadRequestException("A guest file path is required.");

            var bytes = await _proxmoxService.DownloadGuestFile(vm.ProxmoxVmInfo, request.Path);

            return new FileContentResult(bytes, ContentType) { FileDownloadName = FileName(request.Path) };
        }

        /// <summary>
        /// The last segment of a guest path, split on either separator because the guest may be Windows
        /// while this API runs on Linux.
        /// </summary>
        internal static string FileName(string path)
        {
            var trimmed = path.TrimEnd('/', '\\');
            var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];

            return string.IsNullOrWhiteSpace(name) ? "download" : name;
        }
    }
}
