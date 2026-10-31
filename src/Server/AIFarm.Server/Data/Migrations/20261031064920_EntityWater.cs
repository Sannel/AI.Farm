using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIFarm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntityWater : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Water",
                table: "world_entities",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Water",
                table: "world_entities");
        }
    }
}
