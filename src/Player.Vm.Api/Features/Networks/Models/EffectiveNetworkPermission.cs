// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;

namespace Player.Vm.Api.Features.Networks
{
    public class EffectiveNetworkPermission
    {
        public Dictionary<string, string> AllowedNetworks { get; set; } = new();
    }
}
