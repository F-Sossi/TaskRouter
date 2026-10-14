using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoDocuments.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class TerminalTasksCompleteAutomatically : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CompletesAutomatically",
                table: "TaskRouterTaskDefinitions",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletesAutomatically",
                table: "TaskRouterTaskDefinitions");
        }
    }
}
