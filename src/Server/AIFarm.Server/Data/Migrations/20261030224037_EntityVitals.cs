using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIFarm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntityVitals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AgeDays",
                table: "world_entities",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Energy",
                table: "world_entities",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mood",
                table: "world_entities",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Living things already in the world start rested and content, at the ages new ones get.
            migrationBuilder.Sql("""
                UPDATE world_entities SET "Mood" = 'Content', "Energy" = 100, "AgeDays" = CASE "Model"
                    WHEN 'Chicken_Female' THEN 400 WHEN 'Chicken_Male' THEN 500
                    WHEN 'Sheep' THEN 1095 WHEN 'Dog' THEN 1460
                    WHEN 'Cow_Female' THEN 1825 WHEN 'Cow_Male' THEN 2190
                    WHEN 'Pig_Female' THEN 730 WHEN 'Pig_Male' THEN 1095
                    WHEN 'Goat_Female' THEN 1460 WHEN 'Goat_Male' THEN 1825
                    WHEN 'Horse_Female' THEN 2920 WHEN 'Horse_Male' THEN 3285
                    WHEN 'Donkey_Female' THEN 3650 WHEN 'Donkey_Male' THEN 4380
                    ELSE 1095 END
                WHERE "Kind" = 'Animal';
                UPDATE world_entities SET "Mood" = 'Content', "Energy" = 100, "AgeDays" = 9125 WHERE "Kind" = 'Player';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgeDays",
                table: "world_entities");

            migrationBuilder.DropColumn(
                name: "Energy",
                table: "world_entities");

            migrationBuilder.DropColumn(
                name: "Mood",
                table: "world_entities");
        }
    }
}
