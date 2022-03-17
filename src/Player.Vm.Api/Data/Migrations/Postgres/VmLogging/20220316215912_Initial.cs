/*
Copyright 2021 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres.VmLogging
{
    public partial class Initial : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:uuid-ossp", ",,");

            migrationBuilder.CreateTable(
                name: "vm_usage_logging_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    team_ids = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    session_name = table.Column<string>(type: "text", nullable: true),
                    session_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vm_usage_logging_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vm_usage_log_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vm_name = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    vm_active_dt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vm_inactive_dt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vm_usage_log_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_vm_usage_log_entries_vm_usage_logging_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "vm_usage_logging_sessions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_vm_usage_log_entries_session_id",
                table: "vm_usage_log_entries",
                column: "session_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vm_usage_log_entries");

            migrationBuilder.DropTable(
                name: "vm_usage_logging_sessions");
        }
    }
}
