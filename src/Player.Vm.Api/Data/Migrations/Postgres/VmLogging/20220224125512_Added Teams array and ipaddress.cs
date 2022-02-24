using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres.VmLogging
{
    public partial class AddedTeamsarrayandipaddress : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "team_id",
                table: "vm_usage_logging_sessions");

            migrationBuilder.DropColumn(
                name: "team_name",
                table: "vm_usage_logging_sessions");

            migrationBuilder.AddColumn<Guid[]>(
                name: "team_ids",
                table: "vm_usage_logging_sessions",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ip_address",
                table: "vm_usage_log_entries",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "team_ids",
                table: "vm_usage_logging_sessions");

            migrationBuilder.DropColumn(
                name: "ip_address",
                table: "vm_usage_log_entries");

            migrationBuilder.AddColumn<Guid>(
                name: "team_id",
                table: "vm_usage_logging_sessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "team_name",
                table: "vm_usage_logging_sessions",
                type: "text",
                nullable: true);
        }
    }
}
