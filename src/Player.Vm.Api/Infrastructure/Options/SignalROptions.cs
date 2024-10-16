// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options;

public class SignalROptions
{
    public bool EnableStatefulReconnect { get; set; } = true;
    public long StatefulReconnectBufferSizeBytes { get; set; } = 100000;
}