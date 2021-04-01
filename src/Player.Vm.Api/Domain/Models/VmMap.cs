// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Player.Vm.Api.Domain.Models
{
    public class VmMap
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid ViewId { get; set; }
        public virtual List<Coordinate> Coordinates { get; set; } = new List<Coordinate>();
        public string Name { get; set; }
        public string ImageUrl { get; set; }
        public List<Guid> TeamIds { get; set; }

        public VmMap Clone()
        {
            var clone = this.MemberwiseClone() as VmMap;
            clone.Id = Guid.Empty;
            clone.Coordinates = new List<Coordinate>();
            foreach (var coord in this.Coordinates)
            {
                clone.Coordinates.Add(coord.Clone());
            }
            return clone;
        }
    }
}