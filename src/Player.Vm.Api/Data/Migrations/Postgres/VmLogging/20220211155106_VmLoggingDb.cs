using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres.VmLogging
{
    public partial class VmLoggingDb : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:uuid-ossp", ",,");

            migrationBuilder.CreateTable(
                name: "team",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    url = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    allowed_networks = table.Column<string[]>(type: "text[]", nullable: true),
                    power_state = table.Column<int>(type: "integer", nullable: false),
                    ip_addresses = table.Column<string[]>(type: "text[]", nullable: true),
                    has_pending_tasks = table.Column<bool>(type: "boolean", nullable: false),
                    console_connection_info = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vm", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vm_logging_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_name = table.Column<string>(type: "text", nullable: true),
                    session_name = table.Column<string>(type: "text", nullable: true),
                    session_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vm_logging_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vm_team",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vm_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vm_team", x => new { x.team_id, x.vm_id });
                    table.ForeignKey(
                        name: "FK_vm_team_team_team_id",
                        column: x => x.team_id,
                        principalTable: "team",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_vm_team_vm_vm_id",
                        column: x => x.vm_id,
                        principalTable: "vm",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vm_team_vm_id",
                table: "vm_team",
                column: "vm_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vm_logging_sessions");

            migrationBuilder.DropTable(
                name: "vm_team");

            migrationBuilder.DropTable(
                name: "team");

            migrationBuilder.DropTable(
                name: "vm");
        }
    }
}
