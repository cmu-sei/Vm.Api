/*
Crucible
Copyright 2020 Carnegie Mellon University.
NO WARRANTY. THIS CARNEGIE MELLON UNIVERSITY AND SOFTWARE ENGINEERING INSTITUTE MATERIAL IS FURNISHED ON AN "AS-IS" BASIS. CARNEGIE MELLON UNIVERSITY MAKES NO WARRANTIES OF ANY KIND, EITHER EXPRESSED OR IMPLIED, AS TO ANY MATTER INCLUDING, BUT NOT LIMITED TO, WARRANTY OF FITNESS FOR PURPOSE OR MERCHANTABILITY, EXCLUSIVITY, OR RESULTS OBTAINED FROM USE OF THE MATERIAL. CARNEGIE MELLON UNIVERSITY DOES NOT MAKE ANY WARRANTY OF ANY KIND WITH RESPECT TO FREEDOM FROM PATENT, TRADEMARK, OR COPYRIGHT INFRINGEMENT.
Released under a MIT (SEI)-style license, please see license.txt or contact permission@sei.cmu.edu for full terms.
[DISTRIBUTION STATEMENT A] This material has been approved for public release and unlimited distribution.  Please see Copyright notice for non-US Government use and distribution.
Carnegie Mellon(R) and CERT(R) are registered in the U.S. Patent and Trademark Office by Carnegie Mellon University.
DM20-0181
*/

// <auto-generated />
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using System;

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    [DbContext(typeof(VmContext))]
    [Migration("20180511164921_Added_UserId_To_Vm")]
    partial class Added_UserId_To_Vm
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("Npgsql:PostgresExtension:uuid-ossp", "'uuid-ossp', '', ''")
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn)
                .HasAnnotation("ProductVersion", "2.0.2-rtm-10011");

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Team", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.HasKey("Id");

                    b.ToTable("teams");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Vm", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<string>("Name")
                        .HasColumnName("name");

                    b.Property<string>("Url")
                        .HasColumnName("url");

                    b.Property<Guid?>("UserId")
                        .HasColumnName("user_id");

                    b.HasKey("Id");

                    b.ToTable("vms");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmTeam", b =>
                {
                    b.Property<Guid>("TeamId")
                        .HasColumnName("team_id");

                    b.Property<Guid>("VmId")
                        .HasColumnName("vm_id");

                    b.HasKey("TeamId", "VmId");

                    b.HasIndex("VmId");

                    b.ToTable("vm_teams");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmTeam", b =>
                {
                    b.HasOne("Player.Vm.Api.Domain.Models.Team", "Team")
                        .WithMany("VmTeams")
                        .HasForeignKey("TeamId")
                        .OnDelete(DeleteBehavior.Cascade);

                    b.HasOne("Player.Vm.Api.Domain.Models.Vm", "Vm")
                        .WithMany("VmTeams")
                        .HasForeignKey("VmId")
                        .OnDelete(DeleteBehavior.Cascade);
                });
#pragma warning restore 612, 618
        }
    }
}
