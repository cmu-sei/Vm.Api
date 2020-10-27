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
    public partial class fix_db_model : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "radius",
                table: "maps");

            migrationBuilder.DropColumn(
                name: "url",
                table: "maps");

            migrationBuilder.DropColumn(
                name: "x_position",
                table: "maps");

            migrationBuilder.DropColumn(
                name: "y_position",
                table: "maps");

            migrationBuilder.CreateTable(
                name: "coordinate",
                columns: table => new
                {
                    coord_id = table.Column<Guid>(nullable: false),
                    map_id = table.Column<Guid>(nullable: false),
                    x_position = table.Column<double>(nullable: false),
                    y_position = table.Column<double>(nullable: false),
                    radius = table.Column<double>(nullable: false),
                    url = table.Column<string>(nullable: true),
                    vm_map_id = table.Column<Guid>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coordinate", x => new { x.coord_id, x.map_id });
                    table.ForeignKey(
                        name: "FK_coordinate_maps_vm_map_id",
                        column: x => x.vm_map_id,
                        principalTable: "maps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_coordinate_vm_map_id",
                table: "coordinate",
                column: "vm_map_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coordinate");

            migrationBuilder.AddColumn<double>(
                name: "radius",
                table: "maps",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "url",
                table: "maps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "x_position",
                table: "maps",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "y_position",
                table: "maps",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}

