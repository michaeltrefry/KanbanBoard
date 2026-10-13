using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemClaim : Migration
    {
        public const string MarkerCollationSql =
            "ALTER TABLE `WorkItems` MODIFY `ClaimMarker` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimExpiresAtUtc",
                table: "WorkItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimMarker",
                table: "WorkItems",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true,
                collation: "utf8mb4_bin");

            // MySql.EntityFrameworkCore drops the column collation from the ADD above, so set it explicitly.
            // Markers must compare byte for byte: under the default case-insensitive collation two markers that
            // differ only by case would count as the same claim.
            migrationBuilder.Sql(MarkerCollationSql);

            migrationBuilder.AddColumn<string>(
                name: "ClaimantName",
                table: "WorkItems",
                type: "varchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimantUserId",
                table: "WorkItems",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimExpiresAtUtc",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "ClaimMarker",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "ClaimantName",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "ClaimantUserId",
                table: "WorkItems");
        }
    }
}
