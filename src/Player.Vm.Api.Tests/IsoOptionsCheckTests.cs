// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Features.Files;
using Xunit;

namespace Player.Vm.Api.Tests;

// The vSphere upload settings moved to Vsphere: as a breaking change, and a legacy key is ignored
// rather than mapped forward - so this log is the only thing standing between an un-migrated
// deployment and an ISO upload that fails for no visible reason. It has to fire, at the right level,
// naming the key the operator has to set.
public class IsoOptionsCheckTests
{
    // Three members, so a hand-written recorder beats a substitute - and unlike NullLogger it can be
    // asserted on. Records the formatted message, which is what an operator actually reads.
    private class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static RecordingLogger Run(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string>(s.Key, s.Value)))
            .Build();

        var logger = new RecordingLogger();
        IsoOptionsCheck.Log(configuration, logger);

        return logger;
    }

    // Enough vSphere config that CheckIneffectiveVsphereSettings stays quiet, so each test's assertions
    // are only about the key it is exercising.
    private static (string, string)[] EnabledHost() =>
    [
        ("Vsphere:Hosts:0:Enabled", "true"),
        ("Vsphere:Hosts:0:Address", "vcenter.example.test")
    ];

    private static (string, string)[] With(params (string, string)[][] groups) =>
        groups.SelectMany(g => g).ToArray();

    [Fact]
    public void MigratedConfiguration_SaysNothing()
    {
        // The state every deployment should end up in - and the state the dev config is already in, so a
        // clean boot has to stay clean or the log stops meaning anything.
        var logger = Run(With(EnabledHost(), [("Vsphere:IsoRoot", "player/isos"), ("Vsphere:UploadViaApi", "true")]));

        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("IsoUpload:BasePath", "Vsphere:IsoRoot", "Vsphere__IsoRoot")]
    [InlineData("IsoUpload:UploadToDatastore", "Vsphere:UploadViaApi", "Vsphere__UploadViaApi")]
    public void ALegacyKeyWithNoReplacement_IsAnErrorNamingTheNewEnvVar(
        string oldKey,
        string newKey,
        string newEnvVar)
    {
        var logger = Run(With(EnabledHost(), [(oldKey, "some-value")]));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains(oldKey));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(newKey, entry.Message);
        Assert.Contains(newEnvVar, entry.Message);
    }

    [Theory]
    [InlineData("IsoUpload:BasePath", "Vsphere:IsoRoot", "player/isos", "IsoUpload__BasePath")]
    [InlineData("IsoUpload:UploadToDatastore", "Vsphere:UploadViaApi", "true", "IsoUpload__UploadToDatastore")]
    public void ALegacyKeyAgreeingWithItsReplacement_IsOnlyAWarningToTidyUp(
        string oldKey,
        string newKey,
        string value,
        string oldEnvVar)
    {
        // The transitional shape a deployment sits in while its environment variables are being renamed:
        // behavior is already correct, so this must not read as breakage.
        var logger = Run(With(EnabledHost(), [(oldKey, value), (newKey, value)]));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains(oldKey));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(oldEnvVar, entry.Message);
    }

    [Theory]
    [InlineData("IsoUpload:BasePath", "Vsphere:IsoRoot", "player/isos", "Vsphere__IsoRoot")]
    [InlineData("IsoUpload:UploadToDatastore", "Vsphere:UploadViaApi", "false", "Vsphere__UploadViaApi")]
    public void ALegacyKeyDisagreeingWithItsReplacement_IsAnErrorNamingBothValues(
        string oldKey,
        string newKey,
        string shippedDefault,
        string newEnvVar)
    {
        // The un-migrated deployment that actually happens: every replacement key ships with a value in
        // appsettings.json, so the operator's override sits on the legacy name while the shipped default
        // is what takes effect. "The new key is set" would be cold comfort - the disagreement is the only
        // in-process evidence that the intended value was lost.
        var logger = Run(With(EnabledHost(), [(oldKey, "operator-set"), (newKey, shippedDefault)]));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains(oldKey));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("operator-set", entry.Message);
        Assert.Contains(shippedDefault, entry.Message);
        Assert.Contains(newEnvVar, entry.Message);
    }

    [Theory]
    [InlineData("IsoUpload:UploadToDatastore", "Vsphere:UploadViaApi", "true", "True")]  // bool spelled two ways
    [InlineData("IsoUpload:BasePath", "Vsphere:IsoRoot", "player/isos", " player/isos ")]
    public void AgreementIgnoresCaseAndSurroundingWhitespace(
        string oldKey,
        string newKey,
        string oldValue,
        string newValue)
    {
        // The two names are one setting spelled twice, so a difference that no binder would see must not
        // be reported as a lost value.
        var logger = Run(With(EnabledHost(), [(oldKey, oldValue), (newKey, newValue)]));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public void ABlankReplacement_CountsAsUnset()
    {
        // An environment-variable deployment cannot remove a key, only blank it, so a blank has to read
        // as "not configured" here - the same contract that makes the UploadViaApi options nullable.
        var logger = Run(With(EnabledHost(), [("IsoUpload:BasePath", "/app/isos/player"), ("Vsphere:IsoRoot", "")]));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("IsoUpload:BasePath"));
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Theory]
    [InlineData("false", "vcenter.example.test")]  // host explicitly disabled
    [InlineData("true", "")]                       // no address to connect to
    public void VsphereIsoSettingsWithNoUsableHost_AreReportedAsIneffective(string enabled, string address)
    {
        // Usually means the value landed under the wrong provider's section - Proxmox ISO storage has its
        // own IsoRoot and UploadViaApi, so the message names them.
        var logger = Run(
            ("Vsphere:IsoRoot", "player/isos"),
            ("Vsphere:Hosts:0:Enabled", enabled),
            ("Vsphere:Hosts:0:Address", address));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("no effect", entry.Message);
        Assert.Contains("Proxmox:IsoRoot", entry.Message);
    }

    [Fact]
    public void NoVsphereIsoSettingsAtAll_IsNotReportedAsIneffective()
    {
        // A Proxmox-only install never set these, so it has nothing to be warned about.
        var logger = Run(("Proxmox:IsoStorage", "nfs"), ("Proxmox:UploadViaApi", "true"));

        Assert.Empty(logger.Entries);
    }
}
