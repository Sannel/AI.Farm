using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIFarm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class NpcMemoryFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_memory_files_world_entities_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "world_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_files_world_entities_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "world_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_memory_files_OwnerId_SubjectId",
                table: "memory_files",
                columns: new[] { "OwnerId", "SubjectId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_memory_files_SubjectId",
                table: "memory_files",
                column: "SubjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_files");
        }
    }
}
