// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Player.Vm.Api.Domain.Models
{
    public class Coordinate
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public double XPosition { get; set; }

        public double YPosition { get; set; }

        public double Radius { get; set; }

        public string[] Urls { get; set; }

        public string Label { get; set; }

        public Coordinate Clone()
        {
            var clone = this.MemberwiseClone() as Coordinate;
            clone.Id = Guid.Empty;
            return clone;
        }
    }
}