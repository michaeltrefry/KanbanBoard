using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItemDependencies",
                columns: table => new
                {
                    BlockedItemId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    BlockerItemId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemDependencies", x => new { x.BlockedItemId, x.BlockerItemId });
                    table.ForeignKey(
                        name: "FK_WorkItemDependencies_WorkItems_BlockedItemId",
                        column: x => x.BlockedItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkItemDependencies_WorkItems_BlockerItemId",
                        column: x => x.BlockerItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemDependencies_BlockerItemId",
                table: "WorkItemDependencies",
                column: "BlockerItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemDependencies");
        }
    }
}
