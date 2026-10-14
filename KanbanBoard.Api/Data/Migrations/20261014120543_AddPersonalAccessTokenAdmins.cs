using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalAccessTokenAdmins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IssuedByName",
                table: "PersonalAccessTokens",
                type: "varchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedByUserId",
                table: "PersonalAccessTokens",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedByName",
                table: "PersonalAccessTokens",
                type: "varchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedByUserId",
                table: "PersonalAccessTokens",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IssuedByName",
                table: "PersonalAccessTokens");

            migrationBuilder.DropColumn(
                name: "IssuedByUserId",
                table: "PersonalAccessTokens");

            migrationBuilder.DropColumn(
                name: "RevokedByName",
                table: "PersonalAccessTokens");

            migrationBuilder.DropColumn(
                name: "RevokedByUserId",
                table: "PersonalAccessTokens");
        }
    }
}
