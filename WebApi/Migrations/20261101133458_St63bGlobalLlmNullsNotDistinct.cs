using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Migrations
{
    /// <inheritdoc />
    public partial class St63bGlobalLlmNullsNotDistinct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LlmSettings_UserId_GlobalUnique",
                table: "LlmSettings");

            migrationBuilder.CreateIndex(
                name: "IX_LlmSettings_UserId_GlobalUnique",
                table: "LlmSettings",
                column: "UserId",
                unique: true,
                filter: "\"UserId\" IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LlmSettings_UserId_GlobalUnique",
                table: "LlmSettings");

            migrationBuilder.CreateIndex(
                name: "IX_LlmSettings_UserId_GlobalUnique",
                table: "LlmSettings",
                column: "UserId",
                unique: true,
                filter: "\"UserId\" IS NULL");
        }
    }
}
