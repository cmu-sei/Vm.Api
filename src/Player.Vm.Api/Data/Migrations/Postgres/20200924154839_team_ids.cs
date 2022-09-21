// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    public partial class team_ids : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<Guid>>(
                name: "team_ids",
                table: "maps",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "team_ids",
                table: "maps");
        }
    }
}

