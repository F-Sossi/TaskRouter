using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoDocuments.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class FilterInboxIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskRouterTasks_AssignedToActorId_Status",
                table: "TaskRouterTasks");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_AssignedToActorId_Status",
                table: "TaskRouterTasks",
                columns: new[] { "AssignedToActorId", "Status" },
                filter: "[Status] IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskRouterTasks_AssignedToActorId_Status",
                table: "TaskRouterTasks");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_AssignedToActorId_Status",
                table: "TaskRouterTasks",
                columns: new[] { "AssignedToActorId", "Status" });
        }
    }
}
