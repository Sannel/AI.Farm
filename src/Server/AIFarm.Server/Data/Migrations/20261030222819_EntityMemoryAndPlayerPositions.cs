using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIFarm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class EntityMemoryAndPlayerPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasMemory",
                table: "world_entities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The animals already in a world keep memories, like newly stocked ones.
            migrationBuilder.Sql("UPDATE world_entities SET \"HasMemory\" = true WHERE \"Kind\" = 'Animal';");

            migrationBuilder.AddColumn<Guid>(
                name: "EntityId",
                table: "memories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "X",
                table: "memories",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Y",
                table: "memories",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Z",
                table: "memories",
                type: "real",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_world_entities_Name",
                table: "world_entities",
                column: "Name",
                unique: true,
                filter: "\"Kind\" = 'Player'");

            migrationBuilder.CreateIndex(
                name: "IX_memories_EntityId",
                table: "memories",
                column: "EntityId");

            migrationBuilder.AddForeignKey(
                name: "FK_memories_world_entities_EntityId",
                table: "memories",
                column: "EntityId",
                principalTable: "world_entities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_memories_world_entities_EntityId",
                table: "memories");

            migrationBuilder.DropIndex(
                name: "IX_world_entities_Name",
                table: "world_entities");

            migrationBuilder.DropIndex(
                name: "IX_memories_EntityId",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "HasMemory",
                table: "world_entities");

            migrationBuilder.DropColumn(
                name: "EntityId",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "X",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Y",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Z",
                table: "memories");
        }
    }
}
