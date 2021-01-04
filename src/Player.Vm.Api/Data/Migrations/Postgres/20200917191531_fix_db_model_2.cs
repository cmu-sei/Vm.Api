// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    public partial class fix_db_model_2 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_coordinate_maps_vm_map_id",
                table: "coordinate");

            migrationBuilder.DropPrimaryKey(
                name: "PK_coordinate",
                table: "coordinate");

            migrationBuilder.DropIndex(
                name: "IX_coordinate_vm_map_id",
                table: "coordinate");

            migrationBuilder.DropColumn(
                name: "coord_id",
                table: "coordinate");

            migrationBuilder.DropColumn(
                name: "map_id",
                table: "coordinate");

            migrationBuilder.DropColumn(
                name: "vm_map_id",
                table: "coordinate");

            migrationBuilder.AddColumn<Guid>(
                name: "id",
                table: "coordinate",
                nullable: false,
                defaultValueSql: "uuid_generate_v4()");

            migrationBuilder.AddPrimaryKey(
                name: "PK_coordinate",
                table: "coordinate",
                column: "id");

            migrationBuilder.CreateTable(
                name: "map_coordinate",
                columns: table => new
                {
                    coord_id = table.Column<Guid>(nullable: false),
                    map_id = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_map_coordinate", x => new { x.coord_id, x.map_id });
                    table.ForeignKey(
                        name: "FK_map_coordinate_coordinate_coord_id",
                        column: x => x.coord_id,
                        principalTable: "coordinate",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_map_coordinate_maps_map_id",
                        column: x => x.map_id,
                        principalTable: "maps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_map_coordinate_map_id",
                table: "map_coordinate",
                column: "map_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "map_coordinate");

            migrationBuilder.DropPrimaryKey(
                name: "PK_coordinate",
                table: "coordinate");

            migrationBuilder.DropColumn(
                name: "id",
                table: "coordinate");

            migrationBuilder.AddColumn<Guid>(
                name: "coord_id",
                table: "coordinate",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "map_id",
                table: "coordinate",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "vm_map_id",
                table: "coordinate",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_coordinate",
                table: "coordinate",
                columns: new[] { "coord_id", "map_id" });

            migrationBuilder.CreateIndex(
                name: "IX_coordinate_vm_map_id",
                table: "coordinate",
                column: "vm_map_id");

            migrationBuilder.AddForeignKey(
                name: "FK_coordinate_maps_vm_map_id",
                table: "coordinate",
                column: "vm_map_id",
                principalTable: "maps",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

