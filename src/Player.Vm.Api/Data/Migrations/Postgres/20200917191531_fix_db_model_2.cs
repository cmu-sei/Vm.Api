/*
Crucible
Copyright 2020 Carnegie Mellon University.
NO WARRANTY. THIS CARNEGIE MELLON UNIVERSITY AND SOFTWARE ENGINEERING INSTITUTE MATERIAL IS FURNISHED ON AN "AS-IS" BASIS. CARNEGIE MELLON UNIVERSITY MAKES NO WARRANTIES OF ANY KIND, EITHER EXPRESSED OR IMPLIED, AS TO ANY MATTER INCLUDING, BUT NOT LIMITED TO, WARRANTY OF FITNESS FOR PURPOSE OR MERCHANTABILITY, EXCLUSIVITY, OR RESULTS OBTAINED FROM USE OF THE MATERIAL. CARNEGIE MELLON UNIVERSITY DOES NOT MAKE ANY WARRANTY OF ANY KIND WITH RESPECT TO FREEDOM FROM PATENT, TRADEMARK, OR COPYRIGHT INFRINGEMENT.
Released under a MIT (SEI)-style license, please see license.txt or contact permission@sei.cmu.edu for full terms.
[DISTRIBUTION STATEMENT A] This material has been approved for public release and unlimited distribution.  Please see Copyright notice for non-US Government use and distribution.
Carnegie Mellon(R) and CERT(R) are registered in the U.S. Patent and Trademark Office by Carnegie Mellon University.
DM20-0181
*/

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

