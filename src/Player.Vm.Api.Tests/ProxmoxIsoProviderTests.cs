// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Microsoft.Extensions.Logging.Abstractions;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Options;
using Player.Vm.Api.Features.Files.Providers;
using Player.Vm.Api.Infrastructure.Exceptions;
using Xunit;

namespace Player.Vm.Api.Tests;

// Only the parts of the provider that need no Proxmox: whether it considers itself enabled, and the
// pre-flight filename rules IsoService runs across every provider before it writes anything anywhere.
public class ProxmoxIsoProviderTests
{
    private static readonly Guid ViewId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ScopeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // The IProxmoxService is left null deliberately: nothing under test here talks to Proxmox, and a
    // null makes it obvious if that ever stops being true.
    private static ProxmoxIsoProvider Provider(ProxmoxOptions options) =>
        new(options, null, NullLogger<ProxmoxIsoProvider>.Instance);

    private static ProxmoxOptions EnabledOptions() => new()
    {
        Enabled = true,
        Host = "pve.example.test",
        IsoStorage = "nfs",
        IsoRoot = "/mnt/pve/nfs/template/iso",
        IsoScopeSeparator = "__"
    };

    [Fact]
    public void Provider_IdentifiesItselfByClusterHost()
    {
        var provider = Provider(EnabledOptions());

        Assert.Equal(VmType.Proxmox, provider.ProviderType);
        Assert.Equal("pve.example.test", provider.ProviderInstanceId);
        Assert.Equal(1, provider.TargetCount);
    }

    // An unconfigured Proxmox section has to leave a vSphere-only install untouched rather than fail its
    // uploads, so a missing IsoStorage disables the provider instead of throwing.
    [Fact]
    public void Disabled_WhenIsoStorageIsMissing()
    {
        var options = EnabledOptions();
        options.IsoStorage = null;

        Assert.False(Provider(options).Enabled);
    }

    [Fact]
    public void Disabled_WhenTheClusterItselfIsDisabled()
    {
        var options = EnabledOptions();
        options.Enabled = false;

        Assert.False(Provider(options).Enabled);
    }

    // IsoEnabled overrides the cluster flag in both directions, so ISOs can be turned off without
    // turning off Proxmox, and on before Proxmox VMs exist.
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(null, true, true)]
    public void IsoEnabled_OverridesTheClusterFlag(bool? isoEnabled, bool enabled, bool expected)
    {
        var options = EnabledOptions();
        options.Enabled = enabled;
        options.IsoEnabled = isoEnabled;

        Assert.Equal(expected, Provider(options).Enabled);
    }

    // The upload API needs a measurable multipart body; an IsoRoot write can consume the request stream.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RequiresStagedFile_TracksTheWriteMode(bool uploadToStorage, bool expected)
    {
        var options = EnabledOptions();
        options.UploadToStorage = uploadToStorage;

        Assert.Equal(expected, Provider(options).RequiresStagedFile);
    }

    [Fact]
    public void NormalizeFilename_MatchesWhatPveWouldDoToTheName()
    {
        Assert.Equal("Win_10_x64_.iso", Provider(EnabledOptions()).NormalizeFilename("Win 10 (x64).iso"));
    }

    [Fact]
    public void ValidateFilename_AcceptsANormalName()
    {
        Provider(EnabledOptions()).ValidateFilename(ViewId, ScopeId.ToString(), "tools.iso");
    }

    // A filename carrying the separator would make the stored name decode as too many segments.
    [Fact]
    public void ValidateFilename_RejectsTheSeparator()
    {
        var ex = Assert.Throws<BadRequestException>(
            () => Provider(EnabledOptions()).ValidateFilename(ViewId, ScopeId.ToString(), "a__b.iso"));

        Assert.Contains("__", ex.Message);
    }

    // A name that normalizes cleanly can never trip the separator check - which is what makes '__' a
    // safe default separator.
    [Fact]
    public void ValidateFilename_AcceptsANormalizedNameThatStartedWithSpacesAndParens()
    {
        var provider = Provider(EnabledOptions());

        provider.ValidateFilename(ViewId, ScopeId.ToString(), provider.NormalizeFilename("Win 10 (x64).iso"));
    }

    // The two GUIDs and two separators eat 74 characters of the 255 available, so a name that uploads
    // fine to vSphere can still be too long here. Rejected up front rather than half-written.
    [Fact]
    public void ValidateFilename_RejectsANameTooLongOnceScopedIntoIt()
    {
        var filename = new string('a', 240) + ".iso";

        var ex = Assert.Throws<BadRequestException>(
            () => Provider(EnabledOptions()).ValidateFilename(ViewId, ScopeId.ToString(), filename));

        Assert.Contains("255", ex.Message);
    }

    // Fail-fast startup validation, but only for deployments that opted in.
    [Fact]
    public void Construction_ThrowsWhenIsoRootIsMissingInNfsMode()
    {
        var options = EnabledOptions();
        options.IsoRoot = null;

        Assert.Throws<InvalidOperationException>(() => Provider(options));
    }

    [Fact]
    public void Construction_ThrowsWhenTheSeparatorIsEmpty()
    {
        var options = EnabledOptions();
        options.IsoScopeSeparator = "";

        Assert.Throws<InvalidOperationException>(() => Provider(options));
    }

    // In API mode PVE would rewrite a separator outside [-a-zA-Z0-9_.], so every stored name would
    // decode differently from the one delete and mount rebuild. Caught at startup, not at upload.
    [Fact]
    public void Construction_ThrowsWhenTheSeparatorWouldNotSurvivePvesUploadApi()
    {
        var options = EnabledOptions();
        options.UploadToStorage = true;
        options.IsoScopeSeparator = "#";

        var ex = Assert.Throws<InvalidOperationException>(() => Provider(options));
        Assert.Contains("__", ex.Message);
    }

    // The same '#' separator is fine when writing over NFS, where nothing rewrites the name.
    [Fact]
    public void Construction_AllowsANonPveSafeSeparatorInNfsMode()
    {
        var options = EnabledOptions();
        options.IsoScopeSeparator = "#";

        Assert.True(Provider(options).Enabled);
    }

    // A deployment that never opted in must not be blocked from starting by ISO settings it has no
    // reason to have filled in.
    [Fact]
    public void Construction_SkipsValidationWhenDisabled()
    {
        var options = new ProxmoxOptions { Enabled = false };

        Assert.False(Provider(options).Enabled);
    }
}
