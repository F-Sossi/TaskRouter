using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoDocuments.Server.Data.Migrations
{
    /// <summary>
    /// Adds the per-version switch deciding who an unroled step goes to when nobody is
    /// named for it.
    ///
    /// <para><b>This changes how existing workflows behave.</b> The column defaults to
    /// false, which is "leave it unassigned" — so every version already in the database
    /// stops handing the next step to whoever finished the last one, and starts leaving it
    /// as unclaimed work in its section. That is deliberate: one rule everywhere beats the
    /// same screen behaving two ways depending on when the workflow was authored. A
    /// workflow that genuinely wants the old behaviour turns the switch back on.</para>
    ///
    /// <para>No data is lost either way — the column only affects tasks created after it,
    /// and tasks already assigned keep their assignee.</para>
    /// </summary>
    public partial class CarryAssignmentForward : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CarryAssignmentForward",
                table: "TaskRouterDefinitionVersions",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CarryAssignmentForward",
                table: "TaskRouterDefinitionVersions");
        }
    }
}
