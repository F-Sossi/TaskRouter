using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoDocuments.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskRouterPreAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowRunId = table.Column<int>(type: "int", nullable: false),
                    TaskDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BranchKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterPreAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterPreAssignments_TaskRouterRuns_WorkflowRunId",
                        column: x => x.WorkflowRunId,
                        principalTable: "TaskRouterRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskRouterPreAssignments_TaskRouterTaskDefinitions_TaskDefinitionId",
                        column: x => x.TaskDefinitionId,
                        principalTable: "TaskRouterTaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterPreAssignments_TaskDefinitionId",
                table: "TaskRouterPreAssignments",
                column: "TaskDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterPreAssignments_WorkflowRunId_TaskDefinitionId",
                table: "TaskRouterPreAssignments",
                columns: new[] { "WorkflowRunId", "TaskDefinitionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskRouterPreAssignments");
        }
    }
}
