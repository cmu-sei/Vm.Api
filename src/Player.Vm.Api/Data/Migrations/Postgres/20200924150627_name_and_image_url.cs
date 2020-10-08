using Microsoft.EntityFrameworkCore.Migrations;

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    public partial class name_and_image_url : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "image_url",
                table: "maps",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "maps",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "image_url",
                table: "maps");

            migrationBuilder.DropColumn(
                name: "name",
                table: "maps");
        }
    }
}
