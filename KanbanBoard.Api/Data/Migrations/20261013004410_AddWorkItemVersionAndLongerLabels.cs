using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemVersionAndLongerLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Labels",
                table: "WorkItems",
                type: "varchar(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(240)",
                oldMaxLength: 240,
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "WorkItems",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "WorkItems");

            migrationBuilder.AlterColumn<string>(
                name: "Labels",
                table: "WorkItems",
                type: "varchar(240)",
                maxLength: 240,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(4000)",
                oldMaxLength: 4000,
                oldNullable: true);
        }
    }
}
