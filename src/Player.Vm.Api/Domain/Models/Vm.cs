// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Player.Vm.Api.Infrastructure.Options;
using Player.Vm.Api.Infrastructure.Serialization;

namespace Player.Vm.Api.Domain.Models
{
    public class Vm
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public string Url { get; set; }

        public string Name { get; set; }

        public Guid? UserId { get; set; }

        public virtual ICollection<VmTeam> VmTeams { get; set; } = new List<VmTeam>();

        public string[] AllowedNetworks { get; set; }

        public PowerState PowerState { get; set; }

        public string[] IpAddresses { get; set; }

        public bool HasPendingTasks { get; set; }

        public ConsoleConnectionInfo ConsoleConnectionInfo { get; set; }

        public bool TeamsLoaded
        {
            get
            {
                return this.VmTeams != null && this.VmTeams.Count > 0;
            }
        }

        public string GetUrl(ConsoleUrlOptions options)
        {
            // append guacamole params to url
            if (this.ConsoleConnectionInfo != null && !string.IsNullOrEmpty(this.Url))
            {
                // guacamole expects the last part of the url to be a base64 encoded string combining the
                // connection id, connection type (c for connection, g for group), and provider name
                // separated by null characters
                var connectionId = Convert.ToBase64String(
                    Encoding.Default.GetBytes(
                    $"{this.Id}{Convert.ToChar(0x0)}c{Convert.ToChar(0x0)}{options.Guacamole.ProviderName}"));

                var guacamoleUrlFragment = $"/#/client/{connectionId}";

                return $"{this.Url}{guacamoleUrlFragment}";
            }

            if (!string.IsNullOrEmpty(this.Url))
            {
                return this.Url;
            }

            return $"{options.Vsphere.Url}/vm/{this.Id}/console";
        }
    }

    public enum PowerState
    {
        Unknown,
        On,
        Off,
        Suspended
    }


    public class VmConfiguration : IEntityTypeConfiguration<Vm>
    {
        public void Configure(EntityTypeBuilder<Vm> builder)
        {
            builder
                .Property(x => x.ConsoleConnectionInfo)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, DefaultJsonSettings.Settings),
                    v => JsonSerializer.Deserialize<ConsoleConnectionInfo>(v, DefaultJsonSettings.Settings));
        }
    }
}
