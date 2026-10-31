using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIFarm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class NpcPersonas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Persona",
                table: "world_entities",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Persona",
                table: "world_entities");
        }
    }
}
