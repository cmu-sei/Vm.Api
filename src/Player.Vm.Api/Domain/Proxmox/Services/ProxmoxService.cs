// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Extension;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Cluster;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Domain.Vsphere.Options;

namespace Player.Vm.Api.Domain.Proxmox.Services;

public interface IProxmoxService
{
    Task<ProxmoxConsole> GetConsole(ProxmoxVmInfo info);
    Task<IEnumerable<IClusterResourceVm>> GetVms();
}

public class ProxmoxService : IProxmoxService
{
    private readonly ProxmoxOptions _options;
    private readonly ILogger<ProxmoxService> _logger;
    private readonly PveClient _pveClient;
    private readonly IProxmoxStateService _proxmoxStateService;
    private readonly RewriteHostOptions _rewriteHostOptions;

    public ProxmoxService(
            ProxmoxOptions options,
            ILogger<ProxmoxService> logger,
            IProxmoxStateService proxmoxStateService,
            RewriteHostOptions rewriteHostOptions
        )
    {
        _options = options;
        _logger = logger;
        _proxmoxStateService = proxmoxStateService;
        _rewriteHostOptions = rewriteHostOptions;

        _pveClient = new PveClient(options.Host, _options.Port);
        _pveClient.ApiToken = options.Token;
    }

    public async Task<ProxmoxConsole> GetConsole(ProxmoxVmInfo info)
    {
        var result = await VncProxyCall(info.Node, info.Id, info.Type);
        var success = result.IsSuccessStatusCode;

        if (!success)
        {
            // Check if vm exists on a different node and try again
            var vm = await _pveClient.GetVmAsync(info.Id);

            if (vm != null)
            {
                await _proxmoxStateService.UpdateVm(vm);

                if (vm.IsRunning)
                {
                    info.Node = vm.Node;
                    result = await VncProxyCall(info.Node, info.Id, info.Type);
                    success = result.IsSuccessStatusCode;
                }
            }

            if (!success)
            {
                throw new Exception(result.GetError());
            }
        }

        string url = null;
        string urlFragment = $"/api2/json/nodes/{info.Node}/{info.Type.ToString().ToLower()}/{info.Id}/vncwebsocket?port={result.Response.data.port}&vncticket={WebUtility.UrlEncode(result.Response.data.ticket)}";

        if (_rewriteHostOptions.RewriteHost)
        {
            url = $"wss://{_rewriteHostOptions.RewriteHostUrl}{urlFragment}&{_rewriteHostOptions.RewriteHostQueryParam}={_options.Host}";
        }
        else
        {
            url = $"wss://{_options.Host}{urlFragment}";
        }

        return new ProxmoxConsole()
        {
            Ticket = result.Response.data.ticket,
            Url = url
        };
    }

    private async Task<Result> VncProxyCall(string node, int id, ProxmoxVmType type)
    {
        if (type == ProxmoxVmType.LXC)
        {
            return await _pveClient.Nodes[node].Lxc[id].Vncproxy.Vncproxy(websocket: true);
        }
        else
        {
            return await _pveClient.Nodes[node].Qemu[id].Vncproxy.Vncproxy(websocket: true);
        }
    }

    public async Task<IEnumerable<IClusterResourceVm>> GetVms()
    {
        return await _pveClient.GetResourcesAsync(ClusterResourceType.Vm);
    }

    private async Task<IClusterResourceVm> RefreshVm(int id)
    {
        var vm = await _pveClient.GetVmAsync(id);

        return vm;
    }
}
