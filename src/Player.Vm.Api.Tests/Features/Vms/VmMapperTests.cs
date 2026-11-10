// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Text;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Features.Vms;
using Player.Vm.Api.Tests.Support;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests.Features.Vms;

/// <summary>
/// <see cref="VmMapper.ToVm"/>'s console url, under <see cref="TestConsoleUrls"/>. The endpoint tests run
/// with one default url for every provider, so which base each provider is given is asserted here.
/// </summary>
public class VmMapperTests
{
    [Theory]
    [InlineData(VmType.Vsphere, TestConsoleUrls.VsphereUrl)]
    [InlineData(VmType.Proxmox, TestConsoleUrls.ProxmoxUrl)]
    [InlineData(VmType.Unknown, TestConsoleUrls.DefaultUrl)]
    [InlineData(VmType.Azure, TestConsoleUrls.DefaultUrl)]
    public void A_vm_without_a_url_is_opened_through_its_providers_console(VmType type, string baseUrl)
    {
        var vm = new VmEntity { Id = Guid.NewGuid(), Type = type };

        var model = vm.ToVm(TestConsoleUrls.Options);

        Assert.Equal($"{baseUrl}/vm/{vm.Id}/console", model.Url);
        Assert.True(model.DefaultUrl);
    }

    [Fact]
    public void A_vm_with_console_details_is_opened_through_guacamole_under_the_configured_provider()
    {
        var vm = new VmEntity { Id = Guid.NewGuid(), Url = "https://guacamole.test", ConsoleConnectionInfo = new Player.Vm.Api.Domain.Models.ConsoleConnectionInfo() };

        var model = vm.ToVm(TestConsoleUrls.Options);

        var connectionId = Convert.ToBase64String(Encoding.Default.GetBytes($"{vm.Id}\0c\0guacamole"));
        Assert.Equal($"https://guacamole.test/#/client/{connectionId}", model.Url);
    }
}
