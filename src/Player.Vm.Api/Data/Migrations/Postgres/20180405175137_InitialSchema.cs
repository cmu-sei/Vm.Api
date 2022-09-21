// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore.Migrations;
using System;

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    public partial class InitialSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:uuid-ossp", "'uuid-ossp', '', ''");

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<Guid>(nullable: false, defaultValueSql: "uuid_generate_v4()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vms",
                columns: table => new
                {
                    id = table.Column<Guid>(nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    name = table.Column<string>(nullable: true),
                    url = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vm_teams",
                columns: table => new
                {
                    team_id = table.Column<Guid>(nullable: false),
                    vm_id = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vm_teams", x => new { x.team_id, x.vm_id });
                    table.ForeignKey(
                        name: "FK_vm_teams_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_vm_teams_vms_vm_id",
                        column: x => x.vm_id,
                        principalTable: "vms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vm_teams_vm_id",
                table: "vm_teams",
                column: "vm_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vm_teams");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropTable(
                name: "vms");
        }
    }
}
