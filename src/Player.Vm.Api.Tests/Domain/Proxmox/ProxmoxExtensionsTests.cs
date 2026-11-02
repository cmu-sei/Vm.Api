// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Player.Vm.Api.Domain.Proxmox.Extensions;

namespace Player.Vm.Api.Tests.Domain.Proxmox;

public class ProxmoxExtensionsTests
{
    // The name a Proxmox failure reaches the user under: the UI's own label for the action, never the
    // identifiers the caller added for the log.
    [Theory]
    [InlineData("PowerOn vmid=100", "Power On")]
    [InlineData("PowerOff vmid=100", "Power Off")]
    [InlineData("Reboot vmid=100", "Reboot")]
    [InlineData("Shutdown vmid=100", "Shutdown")]
    [InlineData("CreateSnapshot vmid=100 name=snap1", "Create Snapshot")]
    [InlineData("RevertSnapshot vmid=100 name=snap1", "Revert to Snapshot")]
    [InlineData("RevertToCurrentSnapshot vmid=100", "Revert")]
    [InlineData("DeleteSnapshot vmid=100 name=snap1", "Delete Snapshot")]
    [InlineData("MountIso vmid=100 drive=ide2", "Mount ISO")]
    [InlineData("ChangeNetwork vmid=100 adapter=net0", "Change Network")]
    [InlineData("UploadIso node=pve1 file=a.iso", "Upload ISO")]
    [InlineData("DeleteIso node=pve1 file=a.iso", "Delete ISO")]
    public void OperationName_IsTheActionsUiLabel(string operation, string expected)
    {
        Assert.Equal(expected, ProxmoxExtensions.OperationName(operation));
    }

    // An operation without a label still loses its identifiers rather than coming out blank.
    [Fact]
    public void OperationName_ForAnUnlabelledOperation_IsItsFirstWord()
    {
        Assert.Equal("Resize", ProxmoxExtensions.OperationName("Resize vmid=100"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void OperationName_ForNoOperation_IsNull(string operation)
    {
        Assert.Null(ProxmoxExtensions.OperationName(operation));
    }
}
