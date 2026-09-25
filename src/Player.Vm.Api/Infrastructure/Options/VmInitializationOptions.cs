// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.ComponentModel.DataAnnotations;

namespace Player.Vm.Api.Infrastructure.Options;

public class VmInitializationOptions
{
    [Range(1, 86400)]
    public int DebounceSeconds { get; set; } = 2;

    [Range(1, 86400)]
    public int MaxWaitSeconds { get; set; } = 5;
}
