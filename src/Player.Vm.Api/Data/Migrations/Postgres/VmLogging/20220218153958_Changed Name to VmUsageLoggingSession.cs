using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres.VmLogging
{
    public partial class ChangedNametoVmUsageLoggingSession : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vm_logging_sessions");

            migrationBuilder.CreateTable(
                name: "vm_usage_logging_sessions",
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
                    table.PrimaryKey("PK_vm_usage_logging_sessions", x => x.id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vm_usage_logging_sessions");

            migrationBuilder.CreateTable(
                name: "vm_logging_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    session_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_name = table.Column<string>(type: "text", nullable: true),
                    session_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vm_logging_sessions", x => x.id);
                });
        }
    }
}
