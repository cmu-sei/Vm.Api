using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres.VmLogging
{
    public partial class VmUsageLogEntryRenameoffields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "machine_open",
                table: "vm_usage_log_entries",
                newName: "vm_in_active_dt");

            migrationBuilder.RenameColumn(
                name: "machine_name",
                table: "vm_usage_log_entries",
                newName: "vm_name");

            migrationBuilder.RenameColumn(
                name: "machine_id",
                table: "vm_usage_log_entries",
                newName: "vm_id");

            migrationBuilder.RenameColumn(
                name: "machine_close",
                table: "vm_usage_log_entries",
                newName: "vm_active_dt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "vm_name",
                table: "vm_usage_log_entries",
                newName: "machine_name");

            migrationBuilder.RenameColumn(
                name: "vm_in_active_dt",
                table: "vm_usage_log_entries",
                newName: "machine_open");

            migrationBuilder.RenameColumn(
                name: "vm_id",
                table: "vm_usage_log_entries",
                newName: "machine_id");

            migrationBuilder.RenameColumn(
                name: "vm_active_dt",
                table: "vm_usage_log_entries",
                newName: "machine_close");
        }
    }
}
