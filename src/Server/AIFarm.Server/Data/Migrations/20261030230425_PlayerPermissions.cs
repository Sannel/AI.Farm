using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIFarm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlayerPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "Permissions",
                table: "world_entities",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "world_entities");
        }
    }
}
