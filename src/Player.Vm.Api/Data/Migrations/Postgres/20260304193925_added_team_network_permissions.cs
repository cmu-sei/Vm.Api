using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class added_team_network_permissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_network_permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    network_name = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_network_permissions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_network_permissions_team_id_network_name_type_external~",
                table: "team_network_permissions",
                columns: new[] { "team_id", "network_name", "type", "external_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_network_permissions");
        }
    }
}
