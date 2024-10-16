/*
Copyright 2022 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    public partial class UrlArray : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "url",
                table: "coordinate");

            migrationBuilder.AddColumn<string[]>(
                name: "urls",
                table: "coordinate",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "urls",
                table: "coordinate");

            migrationBuilder.AddColumn<string>(
                name: "url",
                table: "coordinate",
                type: "text",
                nullable: true);
        }
    }
}
