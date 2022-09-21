// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Player.Vm.Api.Features.Vms
{
    public class Coordinate
    {
        public Guid Id { get; set; }

        public double XPosition { get; set; }

        public double YPosition { get; set; }

        public double Radius { get; set; }

        public string[] Urls { get; set; }

        public string Label { get; set; }
    }
}