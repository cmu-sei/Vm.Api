// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    public partial class Added_ConsoleConnectionInfo_To_Vm : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "console_connection_info",
                table: "vms",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "console_connection_info",
                table: "vms");
        }
    }
}
