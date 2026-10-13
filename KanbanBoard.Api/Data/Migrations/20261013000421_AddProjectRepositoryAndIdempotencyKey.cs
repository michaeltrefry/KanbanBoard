using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectRepositoryAndIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultBranch",
                table: "Projects",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKeyHash",
                table: "Projects",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryUrl",
                table: "Projects",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_IdempotencyKeyHash",
                table: "Projects",
                column: "IdempotencyKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_IdempotencyKeyHash",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DefaultBranch",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "IdempotencyKeyHash",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RepositoryUrl",
                table: "Projects");
        }
    }
}
