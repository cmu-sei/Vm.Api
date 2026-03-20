// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class added_view_networks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "view_networks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    view_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_type = table.Column<int>(type: "integer", nullable: false),
                    provider_instance_id = table.Column<string>(type: "text", nullable: true),
                    network_id = table.Column<string>(type: "text", nullable: true),
                    team_ids = table.Column<Guid[]>(type: "uuid[]", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_view_networks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_view_networks_view_id_provider_type_provider_instance_id_ne~",
                table: "view_networks",
                columns: new[] { "view_id", "provider_type", "provider_instance_id", "network_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "view_networks");
        }
    }
}
