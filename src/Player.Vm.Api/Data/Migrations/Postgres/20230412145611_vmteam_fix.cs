// Copyright 2023 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    public partial class vmteam_fix : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_vm_teams_teams_team_id",
                table: "vm_teams");

            migrationBuilder.DropTable(
                name: "teams");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.id);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_vm_teams_teams_team_id",
                table: "vm_teams",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
