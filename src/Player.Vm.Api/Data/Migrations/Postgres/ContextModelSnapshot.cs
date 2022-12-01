﻿// <auto-generated />
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Player.Vm.Api.Data;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    [DbContext(typeof(VmContext))]
    partial class ContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "6.0.1")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.HasPostgresExtension(modelBuilder, "uuid-ossp");
            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Player.Api.Client.WebhookEvent", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<string>("Payload")
                        .HasColumnType("text")
                        .HasColumnName("payload");

                    b.Property<DateTimeOffset>("Timestamp")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("timestamp");

                    b.Property<int>("Type")
                        .HasColumnType("integer")
                        .HasColumnName("type");

                    b.HasKey("Id");

                    b.ToTable("webhook_events");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Coordinate", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<string>("Label")
                        .HasColumnType("text")
                        .HasColumnName("label");

                    b.Property<double>("Radius")
                        .HasColumnType("double precision")
                        .HasColumnName("radius");

                    b.Property<string[]>("Urls")
                        .HasColumnType("text[]")
                        .HasColumnName("urls");

                    b.Property<Guid?>("VmMapId")
                        .HasColumnType("uuid")
                        .HasColumnName("vm_map_id");

                    b.Property<double>("XPosition")
                        .HasColumnType("double precision")
                        .HasColumnName("x_position");

                    b.Property<double>("YPosition")
                        .HasColumnType("double precision")
                        .HasColumnName("y_position");

                    b.HasKey("Id");

                    b.HasIndex("VmMapId");

                    b.ToTable("coordinate");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.ProxmoxVmInfo", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("Node")
                        .HasColumnType("text")
                        .HasColumnName("node");

                    b.Property<int>("Type")
                        .HasColumnType("integer")
                        .HasColumnName("type");

                    b.Property<Guid>("VmId")
                        .HasColumnType("uuid")
                        .HasColumnName("vm_id");

                    b.HasKey("Id");

                    b.HasIndex("VmId")
                        .IsUnique();

                    b.ToTable("proxmox_vm_info");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Team", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.HasKey("Id");

                    b.ToTable("teams");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Vm", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<string[]>("AllowedNetworks")
                        .HasColumnType("text[]")
                        .HasColumnName("allowed_networks");

                    b.Property<string>("ConsoleConnectionInfo")
                        .HasColumnType("text")
                        .HasColumnName("console_connection_info");

                    b.Property<bool>("Embeddable")
                        .HasColumnType("boolean")
                        .HasDefaultValue(true)
                        .HasColumnName("embeddable");

                    b.Property<bool>("HasPendingTasks")
                        .HasColumnType("boolean")
                        .HasColumnName("has_pending_tasks");

                    b.Property<string[]>("IpAddresses")
                        .HasColumnType("text[]")
                        .HasColumnName("ip_addresses");

                    b.Property<string>("Name")
                        .HasColumnType("text")
                        .HasColumnName("name");

                    b.Property<int>("PowerState")
                        .HasColumnType("integer")
                        .HasColumnName("power_state");

                    b.Property<int>("Type")
                        .HasColumnType("integer")
                        .HasColumnName("type");

                    b.Property<string>("Url")
                        .HasColumnType("text")
                        .HasColumnName("url");

                    b.Property<Guid?>("UserId")
                        .HasColumnType("uuid")
                        .HasColumnName("user_id");

                    b.HasKey("Id");

                    b.ToTable("vms");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmMap", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<string>("ImageUrl")
                        .HasColumnType("text")
                        .HasColumnName("image_url");

                    b.Property<string>("Name")
                        .HasColumnType("text")
                        .HasColumnName("name");

                    b.Property<List<Guid>>("TeamIds")
                        .HasColumnType("uuid[]")
                        .HasColumnName("team_ids");

                    b.Property<Guid>("ViewId")
                        .HasColumnType("uuid")
                        .HasColumnName("view_id");

                    b.HasKey("Id");

                    b.ToTable("maps");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmTeam", b =>
                {
                    b.Property<Guid>("TeamId")
                        .HasColumnType("uuid")
                        .HasColumnName("team_id");

                    b.Property<Guid>("VmId")
                        .HasColumnType("uuid")
                        .HasColumnName("vm_id");

                    b.HasKey("TeamId", "VmId");

                    b.HasIndex("VmId");

                    b.ToTable("vm_teams");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Coordinate", b =>
                {
                    b.HasOne("Player.Vm.Api.Domain.Models.VmMap", null)
                        .WithMany("Coordinates")
                        .HasForeignKey("VmMapId");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.ProxmoxVmInfo", b =>
                {
                    b.HasOne("Player.Vm.Api.Domain.Models.Vm", null)
                        .WithOne("ProxmoxVmInfo")
                        .HasForeignKey("Player.Vm.Api.Domain.Models.ProxmoxVmInfo", "VmId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
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

                    b.Navigation("Team");

                    b.Navigation("Vm");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Team", b =>
                {
                    b.Navigation("VmTeams");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Vm", b =>
                {
                    b.Navigation("ProxmoxVmInfo");

                    b.Navigation("VmTeams");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmMap", b =>
                {
                    b.Navigation("Coordinates");
                });
#pragma warning restore 612, 618
        }
    }
}
