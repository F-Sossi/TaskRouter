using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoDocuments.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewConsistencyGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM TaskRouterVariables WHERE TaskId IS NULL GROUP BY WorkflowRunId, Name HAVING COUNT(*) > 1)
                    THROW 51000, 'Reconcile duplicate run variables before applying ReviewConsistencyGuards.', 1;
                IF EXISTS (SELECT 1 FROM TaskRouterDefinitionVersions WHERE IsPublished = 1 AND IsLatest = 1 AND IsArchived = 0 GROUP BY WorkflowDefinitionId HAVING COUNT(*) > 1)
                    THROW 51000, 'Reconcile multiple latest published versions before applying ReviewConsistencyGuards.', 1;
                """);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderLastCheckedAt",
                table: "TaskRouterTasks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventPayload",
                table: "TaskRouterOutbox",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterVariables_WorkflowRunId_Name",
                table: "TaskRouterVariables",
                columns: new[] { "WorkflowRunId", "Name" },
                unique: true,
                filter: "[TaskId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_ReminderLastCheckedAt_Id",
                table: "TaskRouterTasks",
                columns: new[] { "ReminderLastCheckedAt", "Id" },
                filter: "[ReminderSentAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterDefinitionVersions_WorkflowDefinitionId",
                table: "TaskRouterDefinitionVersions",
                column: "WorkflowDefinitionId",
                unique: true,
                filter: "[IsPublished] = 1 AND [IsLatest] = 1 AND [IsArchived] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskRouterVariables_WorkflowRunId_Name",
                table: "TaskRouterVariables");

            migrationBuilder.DropIndex(
                name: "IX_TaskRouterTasks_ReminderLastCheckedAt_Id",
                table: "TaskRouterTasks");

            migrationBuilder.DropIndex(
                name: "IX_TaskRouterDefinitionVersions_WorkflowDefinitionId",
                table: "TaskRouterDefinitionVersions");

            migrationBuilder.DropColumn(
                name: "ReminderLastCheckedAt",
                table: "TaskRouterTasks");

            migrationBuilder.DropColumn(
                name: "EventPayload",
                table: "TaskRouterOutbox");
        }
    }
}
