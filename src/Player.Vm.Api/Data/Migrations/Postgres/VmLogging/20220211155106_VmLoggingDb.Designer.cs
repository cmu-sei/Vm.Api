﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Player.Vm.Api.Data;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres.VmLogging
{
    [DbContext(typeof(VmLoggingContext))]
    [Migration("20220211155106_VmLoggingDb")]
    partial class VmLoggingDb
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "6.0.1")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.HasPostgresExtension(modelBuilder, "uuid-ossp");
            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.Team", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.HasKey("Id");

                    b.ToTable("team");
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

                    b.Property<string>("Url")
                        .HasColumnType("text")
                        .HasColumnName("url");

                    b.Property<Guid?>("UserId")
                        .HasColumnType("uuid")
                        .HasColumnName("user_id");

                    b.HasKey("Id");

                    b.ToTable("vm");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmLoggingSession", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<DateTimeOffset>("SessionEnd")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("session_end");

                    b.Property<string>("SessionName")
                        .HasColumnType("text")
                        .HasColumnName("session_name");

                    b.Property<DateTimeOffset>("SessionStart")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("session_start");

                    b.Property<Guid>("TeamId")
                        .HasColumnType("uuid")
                        .HasColumnName("team_id");

                    b.Property<string>("TeamName")
                        .HasColumnType("text")
                        .HasColumnName("team_name");

                    b.HasKey("Id");

                    b.ToTable("vm_logging_sessions");
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

                    b.ToTable("vm_team");
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
                    b.Navigation("VmTeams");
                });
#pragma warning restore 612, 618
        }
    }
}