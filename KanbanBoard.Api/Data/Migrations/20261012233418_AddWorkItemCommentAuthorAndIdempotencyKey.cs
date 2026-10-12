using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemCommentAuthorAndIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Author",
                table: "WorkItemComments",
                type: "varchar(320)",
                maxLength: 320,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(80)",
                oldMaxLength: 80);

            migrationBuilder.AddColumn<string>(
                name: "AuthorUserId",
                table: "WorkItemComments",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKeyHash",
                table: "WorkItemComments",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemComments_WorkItemId_IdempotencyKeyHash",
                table: "WorkItemComments",
                columns: new[] { "WorkItemId", "IdempotencyKeyHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItemComments_WorkItemId_IdempotencyKeyHash",
                table: "WorkItemComments");

            migrationBuilder.DropColumn(
                name: "AuthorUserId",
                table: "WorkItemComments");

            migrationBuilder.DropColumn(
                name: "IdempotencyKeyHash",
                table: "WorkItemComments");

            migrationBuilder.AlterColumn<string>(
                name: "Author",
                table: "WorkItemComments",
                type: "varchar(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(320)",
                oldMaxLength: 320);
        }
    }
}
