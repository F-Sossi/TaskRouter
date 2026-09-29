using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoDocuments.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RouteOutcomeById : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited, and the order is the point. The scaffold dropped OutcomeKey first,
            // which throws away the only thing saying which outcome each route fires on --
            // every route in the database would have survived as a row pointing at nothing.
            //
            // Add, backfill, prune, then drop.
            migrationBuilder.AddColumn<int>(
                name: "TaskOutcomeDefinitionId",
                table: "TaskRouterTaskRoutes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // The join is the same pair the engine used to match a route: the task definition
            // and the key. Plain '=' rather than an explicit collation, so case resolves
            // exactly as routing did on this database before the change -- a case-insensitive
            // collation matched 'Approved' to 'approved', and this keeps doing so rather than
            // pruning routes that were working yesterday.
            migrationBuilder.Sql("""
                UPDATE r
                   SET r.TaskOutcomeDefinitionId = o.Id
                  FROM TaskRouterTaskRoutes r
                  JOIN TaskRouterTaskOutcomes o
                    ON o.TaskDefinitionId = r.TaskDefinitionId
                   AND o.OutcomeKey = r.OutcomeKey;
                """);

            // Routes naming an outcome their task does not declare.
            //
            // Deleting them loses nothing: completion validates the outcome against the
            // task's declared outcomes before any route is looked for, so one of these could
            // never fire. They are the authoring mistake this change makes unwritable, and
            // they have to go before the foreign key can be added.
            migrationBuilder.Sql(
                "DELETE FROM TaskRouterTaskRoutes WHERE TaskOutcomeDefinitionId = 0;");

            migrationBuilder.DropColumn(
                name: "OutcomeKey",
                table: "TaskRouterTaskRoutes");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTaskRoutes_TaskOutcomeDefinitionId",
                table: "TaskRouterTaskRoutes",
                column: "TaskOutcomeDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskRouterTaskRoutes_TaskRouterTaskOutcomes_TaskOutcomeDefinitionId",
                table: "TaskRouterTaskRoutes",
                column: "TaskOutcomeDefinitionId",
                principalTable: "TaskRouterTaskOutcomes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskRouterTaskRoutes_TaskRouterTaskOutcomes_TaskOutcomeDefinitionId",
                table: "TaskRouterTaskRoutes");

            migrationBuilder.DropIndex(
                name: "IX_TaskRouterTaskRoutes_TaskOutcomeDefinitionId",
                table: "TaskRouterTaskRoutes");

            migrationBuilder.AddColumn<string>(
                name: "OutcomeKey",
                table: "TaskRouterTaskRoutes",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // The same shape in reverse, so a rollback leaves routing working rather than
            // leaving every route with an empty key. The routes pruned on the way up do not
            // come back; they could not fire before or after.
            migrationBuilder.Sql("""
                UPDATE r
                   SET r.OutcomeKey = o.OutcomeKey
                  FROM TaskRouterTaskRoutes r
                  JOIN TaskRouterTaskOutcomes o
                    ON o.Id = r.TaskOutcomeDefinitionId;
                """);

            migrationBuilder.DropColumn(
                name: "TaskOutcomeDefinitionId",
                table: "TaskRouterTaskRoutes");
        }
    }
}
