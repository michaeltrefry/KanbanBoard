using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItemLinks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    WorkItemId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Url = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false),
                    UrlHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemLinks_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemLinks_UrlHash",
                table: "WorkItemLinks",
                column: "UrlHash");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemLinks_WorkItemId_Kind_UrlHash",
                table: "WorkItemLinks",
                columns: new[] { "WorkItemId", "Kind", "UrlHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemLinks");
        }
    }
}
