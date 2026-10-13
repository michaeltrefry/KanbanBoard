using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCreateIdempotencyKeysAndEpicPlanImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKeyHash",
                table: "WorkItems",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKeyHash",
                table: "Epics",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKeyHash",
                table: "EpicDocuments",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EpicPlanImports",
                columns: table => new
                {
                    EpicId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ProjectId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    IdempotencyKeyHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "longtext", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicPlanImports", x => x.EpicId);
                    table.ForeignKey(
                        name: "FK_EpicPlanImports_Epics_EpicId",
                        column: x => x.EpicId,
                        principalTable: "Epics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_IdempotencyKeyHash",
                table: "WorkItems",
                columns: new[] { "ProjectId", "IdempotencyKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Epics_ProjectId_IdempotencyKeyHash",
                table: "Epics",
                columns: new[] { "ProjectId", "IdempotencyKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpicDocuments_EpicId_IdempotencyKeyHash",
                table: "EpicDocuments",
                columns: new[] { "EpicId", "IdempotencyKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpicPlanImports_ProjectId_IdempotencyKeyHash",
                table: "EpicPlanImports",
                columns: new[] { "ProjectId", "IdempotencyKeyHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpicPlanImports");

            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId_IdempotencyKeyHash",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_Epics_ProjectId_IdempotencyKeyHash",
                table: "Epics");

            migrationBuilder.DropIndex(
                name: "IX_EpicDocuments_EpicId_IdempotencyKeyHash",
                table: "EpicDocuments");

            migrationBuilder.DropColumn(
                name: "IdempotencyKeyHash",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "IdempotencyKeyHash",
                table: "Epics");

            migrationBuilder.DropColumn(
                name: "IdempotencyKeyHash",
                table: "EpicDocuments");
        }
    }
}
