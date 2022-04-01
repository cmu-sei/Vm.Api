/*
Copyright 2021 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

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
    [Migration("20220401181341_Initial Logging Context")]
    partial class InitialLoggingContext
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "6.0.1")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.HasPostgresExtension(modelBuilder, "uuid-ossp");
            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmUsageLogEntry", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<string>("IpAddress")
                        .HasColumnType("text")
                        .HasColumnName("ip_address");

                    b.Property<Guid>("SessionId")
                        .HasColumnType("uuid")
                        .HasColumnName("session_id");

                    b.Property<Guid>("UserId")
                        .HasColumnType("uuid")
                        .HasColumnName("user_id");

                    b.Property<string>("UserName")
                        .HasColumnType("text")
                        .HasColumnName("user_name");

                    b.Property<DateTimeOffset>("VmActiveDT")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("vm_active_dt");

                    b.Property<Guid>("VmId")
                        .HasColumnType("uuid")
                        .HasColumnName("vm_id");

                    b.Property<DateTimeOffset>("VmInactiveDT")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("vm_inactive_dt");

                    b.Property<string>("VmName")
                        .HasColumnType("text")
                        .HasColumnName("vm_name");

                    b.HasKey("Id");

                    b.HasIndex("SessionId");

                    b.ToTable("vm_usage_log_entries");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmUsageLoggingSession", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id")
                        .HasDefaultValueSql("uuid_generate_v4()");

                    b.Property<DateTimeOffset>("CreatedDt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_dt");

                    b.Property<DateTimeOffset>("SessionEnd")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("session_end");

                    b.Property<string>("SessionName")
                        .HasColumnType("text")
                        .HasColumnName("session_name");

                    b.Property<DateTimeOffset>("SessionStart")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("session_start");

                    b.Property<Guid[]>("TeamIds")
                        .HasColumnType("uuid[]")
                        .HasColumnName("team_ids");

                    b.Property<Guid>("ViewId")
                        .HasColumnType("uuid")
                        .HasColumnName("view_id");

                    b.HasKey("Id");

                    b.ToTable("vm_usage_logging_sessions");
                });

            modelBuilder.Entity("Player.Vm.Api.Domain.Models.VmUsageLogEntry", b =>
                {
                    b.HasOne("Player.Vm.Api.Domain.Models.VmUsageLoggingSession", "Session")
                        .WithMany()
                        .HasForeignKey("SessionId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Session");
                });
#pragma warning restore 612, 618
        }
    }
}
