// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    public partial class Changed_Allowed_Networks_To_Array : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allowed_networks",
                table: "vms"
            );

            migrationBuilder.AddColumn<string[]>(
                name: "allowed_networks",
                table: "vms",
                nullable: true
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allowed_networks",
                table: "vms"
            );

            migrationBuilder.AddColumn<string>(
                name: "allowed_networks",
                table: "vms",
                nullable: true
            );
        }
    }
}
