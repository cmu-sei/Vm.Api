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
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Player.Vm.Api.Data;

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    [DbContext(typeof(VmContext))]
    [Migration("20200917162203_add_vm_map")]
    partial class add_vm_map
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("Npgsql:PostgresExtension:uuid-ossp", ",,")
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .HasAnnotation("ProductVersion", "3.1.4")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Team", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnName("id")
                        .HasColumnType("uuid")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.HasKey("Id");

                    b.ToTable("teams");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Vm", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnName("id")
                        .HasColumnType("uuid")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<string[]>("AllowedNetworks")
                        .HasColumnName("allowed_networks")
                        .HasColumnType("text[]");

                    b.Property<string>("ConsoleConnectionInfo")
                        .HasColumnName("console_connection_info")
                        .HasColumnType("text");

                    b.Property<bool>("HasPendingTasks")
                        .HasColumnName("has_pending_tasks")
                        .HasColumnType("boolean");

                    b.Property<string[]>("IpAddresses")
                        .HasColumnName("ip_addresses")
                        .HasColumnType("text[]");

                    b.Property<string>("Name")
                        .HasColumnName("name")
                        .HasColumnType("text");

                    b.Property<int>("PowerState")
                        .HasColumnName("power_state")
                        .HasColumnType("integer");

                    b.Property<string>("Url")
                        .HasColumnName("url")
                        .HasColumnType("text");

                    b.Property<Guid?>("UserId")
                        .HasColumnName("user_id")
                        .HasColumnType("uuid");

                    b.HasKey("Id");

                    b.ToTable("vms");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmMap", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnName("id")
                        .HasColumnType("uuid")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<double>("Radius")
                        .HasColumnName("radius")
                        .HasColumnType("double precision");

                    b.Property<string>("Url")
                        .HasColumnName("url")
                        .HasColumnType("text");

                    b.Property<double>("XPosition")
                        .HasColumnName("x_position")
                        .HasColumnType("double precision");

                    b.Property<double>("YPosition")
                        .HasColumnName("y_position")
                        .HasColumnType("double precision");

                    b.HasKey("Id");

                    b.ToTable("maps");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmTeam", b =>
                {
                    b.Property<Guid>("TeamId")
                        .HasColumnName("team_id")
                        .HasColumnType("uuid");

                    b.Property<Guid>("VmId")
                        .HasColumnName("vm_id")
                        .HasColumnType("uuid");

                    b.HasKey("TeamId", "VmId");

                    b.HasIndex("VmId");

                    b.ToTable("vm_teams");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmTeam", b =>
                {
                    b.HasOne("Player.Vm.Api.Domain.Models.Team", "Team")
                        .WithMany("VmTeams")
                        .HasForeignKey("TeamId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Player.Vm.Api.Domain.Models.Vm", "Vm")
                        .WithMany("VmTeams")
                        .HasForeignKey("VmId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });
#pragma warning restore 612, 618
        }
    }
}

