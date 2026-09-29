using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DemoDocuments.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Divisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DivisionHeadActorId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Divisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    ActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SectionId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.ActorId);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsSubWorkflow = table.Column<bool>(type: "bit", nullable: false),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterForkManifests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ForkGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<int>(type: "int", nullable: false),
                    SubWorkflowInstanceId = table.Column<int>(type: "int", nullable: true),
                    OriginTaskId = table.Column<int>(type: "int", nullable: false),
                    ConvergenceTaskDefinitionId = table.Column<int>(type: "int", nullable: false),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterForkManifests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TriggerDefinitionId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    Event = table.Column<int>(type: "int", nullable: false),
                    CustomEventName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DispatchMode = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeasedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterTaskTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterTaskTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterTriggerExecutions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TriggerDefinitionId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    Event = table.Column<int>(type: "int", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterTriggerExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterVariables",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowRunId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterVariables", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SectionLeadActorId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DivisionId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sections_Divisions_DivisionId",
                        column: x => x.DivisionId,
                        principalTable: "Divisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DocNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Revision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Comments = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IssueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InternalDueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<int>(type: "int", nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    DocumentTypeDiscriminator = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Originator = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DateEntered = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DateSent = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DateReplyRequiredBy = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DateReplyReceived = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsReplyRequired = table.Column<bool>(type: "bit", nullable: true),
                    IsClassChange = table.Column<bool>(type: "bit", nullable: true),
                    IsRepairChange = table.Column<bool>(type: "bit", nullable: true),
                    Urgency = table.Column<int>(type: "int", nullable: true),
                    CogCoordinatorActorId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DrawingNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SheetCount = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Documents_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterDefinitionVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false),
                    IsLatest = table.Column<bool>(type: "bit", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EntryTaskDefinitionId = table.Column<int>(type: "int", nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterDefinitionVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterDefinitionVersions_TaskRouterDefinitions_WorkflowDefinitionId",
                        column: x => x.WorkflowDefinitionId,
                        principalTable: "TaskRouterDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterForkManifestEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ForkManifestId = table.Column<int>(type: "int", nullable: false),
                    BranchKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AssignedToActorId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BranchOutcomeKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BranchNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterForkManifestEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterForkManifestEntries_TaskRouterForkManifests_ForkManifestId",
                        column: x => x.ForkManifestId,
                        principalTable: "TaskRouterForkManifests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<int>(type: "int", nullable: false),
                    AssignedSectionId = table.Column<int>(type: "int", nullable: false),
                    CurrentlyAssignedActorId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WorkItemState = table.Column<int>(type: "int", nullable: false),
                    CompletionPercentage = table.Column<double>(type: "float", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItems_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkItems_Sections_AssignedSectionId",
                        column: x => x.AssignedSectionId,
                        principalTable: "Sections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    WorkflowDefinitionVersionId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CompletedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsTest = table.Column<bool>(type: "bit", nullable: false),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterRuns_TaskRouterDefinitionVersions_WorkflowDefinitionVersionId",
                        column: x => x.WorkflowDefinitionVersionId,
                        principalTable: "TaskRouterDefinitionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterTaskDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowDefinitionVersionId = table.Column<int>(type: "int", nullable: false),
                    TaskTypeDefinitionId = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    IsAdHoc = table.Column<bool>(type: "bit", nullable: false),
                    IsBlocking = table.Column<bool>(type: "bit", nullable: false),
                    IsForkable = table.Column<bool>(type: "bit", nullable: false),
                    IsConvergencePoint = table.Column<bool>(type: "bit", nullable: false),
                    IsTerminal = table.Column<bool>(type: "bit", nullable: false),
                    AssignmentRoleKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReminderLeadTimeMinutes = table.Column<int>(type: "int", nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterTaskDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterTaskDefinitions_TaskRouterDefinitionVersions_WorkflowDefinitionVersionId",
                        column: x => x.WorkflowDefinitionVersionId,
                        principalTable: "TaskRouterDefinitionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskRouterTaskDefinitions_TaskRouterTaskTypes_TaskTypeDefinitionId",
                        column: x => x.TaskTypeDefinitionId,
                        principalTable: "TaskRouterTaskTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterSubWorkflowAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubWorkflowDefinitionId = table.Column<int>(type: "int", nullable: false),
                    WorkflowDefinitionVersionId = table.Column<int>(type: "int", nullable: false),
                    TaskDefinitionId = table.Column<int>(type: "int", nullable: true),
                    IsAutomatic = table.Column<bool>(type: "bit", nullable: false),
                    IsBlocking = table.Column<bool>(type: "bit", nullable: false),
                    AllowMultiple = table.Column<bool>(type: "bit", nullable: false),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterSubWorkflowAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterSubWorkflowAttachments_TaskRouterDefinitionVersions_WorkflowDefinitionVersionId",
                        column: x => x.WorkflowDefinitionVersionId,
                        principalTable: "TaskRouterDefinitionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskRouterSubWorkflowAttachments_TaskRouterDefinitions_SubWorkflowDefinitionId",
                        column: x => x.SubWorkflowDefinitionId,
                        principalTable: "TaskRouterDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskRouterSubWorkflowAttachments_TaskRouterTaskDefinitions_TaskDefinitionId",
                        column: x => x.TaskDefinitionId,
                        principalTable: "TaskRouterTaskDefinitions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterTaskOutcomes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskDefinitionId = table.Column<int>(type: "int", nullable: false),
                    OutcomeKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterTaskOutcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterTaskOutcomes_TaskRouterTaskDefinitions_TaskDefinitionId",
                        column: x => x.TaskDefinitionId,
                        principalTable: "TaskRouterTaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterTaskRoutes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskDefinitionId = table.Column<int>(type: "int", nullable: false),
                    OutcomeKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NextTaskDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ConditionKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsReworkRoute = table.Column<bool>(type: "bit", nullable: false),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterTaskRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterTaskRoutes_TaskRouterTaskDefinitions_NextTaskDefinitionId",
                        column: x => x.NextTaskDefinitionId,
                        principalTable: "TaskRouterTaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskRouterTaskRoutes_TaskRouterTaskDefinitions_TaskDefinitionId",
                        column: x => x.TaskDefinitionId,
                        principalTable: "TaskRouterTaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowRunId = table.Column<int>(type: "int", nullable: false),
                    TaskDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OutcomeKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompletedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AssignedToActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AssignedBranchKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OverdueFiredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ParentTaskId = table.Column<int>(type: "int", nullable: true),
                    SubWorkflowInstanceId = table.Column<int>(type: "int", nullable: true),
                    ForkGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsForkOrigin = table.Column<bool>(type: "bit", nullable: false),
                    ForkManifestId = table.Column<int>(type: "int", nullable: true),
                    ConvergenceTaskDefinitionId = table.Column<int>(type: "int", nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterTasks_TaskRouterForkManifests_ForkManifestId",
                        column: x => x.ForkManifestId,
                        principalTable: "TaskRouterForkManifests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskRouterTasks_TaskRouterRuns_WorkflowRunId",
                        column: x => x.WorkflowRunId,
                        principalTable: "TaskRouterRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskRouterTasks_TaskRouterTaskDefinitions_TaskDefinitionId",
                        column: x => x.TaskDefinitionId,
                        principalTable: "TaskRouterTaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskRouterTasks_TaskRouterTasks_ParentTaskId",
                        column: x => x.ParentTaskId,
                        principalTable: "TaskRouterTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterTriggerDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskDefinitionId = table.Column<int>(type: "int", nullable: false),
                    TriggerKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Event = table.Column<int>(type: "int", nullable: false),
                    CustomEventName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Configuration = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Condition = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DispatchMode = table.Column<int>(type: "int", nullable: false),
                    FailurePolicy = table.Column<int>(type: "int", nullable: false),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterTriggerDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterTriggerDefinitions_TaskRouterTaskDefinitions_TaskDefinitionId",
                        column: x => x.TaskDefinitionId,
                        principalTable: "TaskRouterTaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterSubWorkflowInstances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubWorkflowDefinitionVersionId = table.Column<int>(type: "int", nullable: false),
                    ParentTaskId = table.Column<int>(type: "int", nullable: false),
                    WorkflowRunId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsBlocking = table.Column<bool>(type: "bit", nullable: false),
                    CompletedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterSubWorkflowInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterSubWorkflowInstances_TaskRouterDefinitionVersions_SubWorkflowDefinitionVersionId",
                        column: x => x.SubWorkflowDefinitionVersionId,
                        principalTable: "TaskRouterDefinitionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskRouterSubWorkflowInstances_TaskRouterTasks_ParentTaskId",
                        column: x => x.ParentTaskId,
                        principalTable: "TaskRouterTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskRouterTaskLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PerformedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifierId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRouterTaskLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRouterTaskLogs_TaskRouterTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TaskRouterTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_DocNumber",
                table: "Documents",
                column: "DocNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_GroupId",
                table: "Documents",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Sections_Code",
                table: "Sections",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sections_DivisionId",
                table: "Sections",
                column: "DivisionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterDefinitionVersions_SubjectType_IsPublished_IsLatest",
                table: "TaskRouterDefinitionVersions",
                columns: new[] { "SubjectType", "IsPublished", "IsLatest" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterDefinitionVersions_WorkflowDefinitionId_Version",
                table: "TaskRouterDefinitionVersions",
                columns: new[] { "WorkflowDefinitionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterForkManifestEntries_ForkManifestId_BranchKey",
                table: "TaskRouterForkManifestEntries",
                columns: new[] { "ForkManifestId", "BranchKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterForkManifests_ForkGroupId",
                table: "TaskRouterForkManifests",
                column: "ForkGroupId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterOutbox_IdempotencyKey",
                table: "TaskRouterOutbox",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterOutbox_Status_NextAttemptAt",
                table: "TaskRouterOutbox",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterRuns_SubjectType_SubjectId",
                table: "TaskRouterRuns",
                columns: new[] { "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterRuns_WorkflowDefinitionVersionId",
                table: "TaskRouterRuns",
                column: "WorkflowDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterSubWorkflowAttachments_SubWorkflowDefinitionId",
                table: "TaskRouterSubWorkflowAttachments",
                column: "SubWorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterSubWorkflowAttachments_TaskDefinitionId",
                table: "TaskRouterSubWorkflowAttachments",
                column: "TaskDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterSubWorkflowAttachments_WorkflowDefinitionVersionId_TaskDefinitionId_SubWorkflowDefinitionId",
                table: "TaskRouterSubWorkflowAttachments",
                columns: new[] { "WorkflowDefinitionVersionId", "TaskDefinitionId", "SubWorkflowDefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterSubWorkflowInstances_ParentTaskId",
                table: "TaskRouterSubWorkflowInstances",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterSubWorkflowInstances_SubWorkflowDefinitionVersionId",
                table: "TaskRouterSubWorkflowInstances",
                column: "SubWorkflowDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterSubWorkflowInstances_WorkflowRunId_Status",
                table: "TaskRouterSubWorkflowInstances",
                columns: new[] { "WorkflowRunId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTaskDefinitions_TaskTypeDefinitionId",
                table: "TaskRouterTaskDefinitions",
                column: "TaskTypeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTaskDefinitions_WorkflowDefinitionVersionId",
                table: "TaskRouterTaskDefinitions",
                column: "WorkflowDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTaskLogs_TaskId",
                table: "TaskRouterTaskLogs",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTaskOutcomes_TaskDefinitionId_OutcomeKey",
                table: "TaskRouterTaskOutcomes",
                columns: new[] { "TaskDefinitionId", "OutcomeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTaskRoutes_NextTaskDefinitionId",
                table: "TaskRouterTaskRoutes",
                column: "NextTaskDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTaskRoutes_TaskDefinitionId",
                table: "TaskRouterTaskRoutes",
                column: "TaskDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_AssignedToActorId_Status",
                table: "TaskRouterTasks",
                columns: new[] { "AssignedToActorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_ForkGroupId",
                table: "TaskRouterTasks",
                column: "ForkGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_ForkManifestId",
                table: "TaskRouterTasks",
                column: "ForkManifestId",
                unique: true,
                filter: "[ForkManifestId] IS NOT NULL AND [Status] <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_ParentTaskId",
                table: "TaskRouterTasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_Status_OverdueFiredAt_DueDate",
                table: "TaskRouterTasks",
                columns: new[] { "Status", "OverdueFiredAt", "DueDate" },
                filter: "[OverdueFiredAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_Status_ReminderSentAt",
                table: "TaskRouterTasks",
                columns: new[] { "Status", "ReminderSentAt" },
                filter: "[ReminderSentAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_SubWorkflowInstanceId",
                table: "TaskRouterTasks",
                column: "SubWorkflowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_TaskDefinitionId",
                table: "TaskRouterTasks",
                column: "TaskDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTasks_WorkflowRunId",
                table: "TaskRouterTasks",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTaskTypes_Key",
                table: "TaskRouterTaskTypes",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTriggerDefinitions_TaskDefinitionId_Event",
                table: "TaskRouterTriggerDefinitions",
                columns: new[] { "TaskDefinitionId", "Event" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTriggerExecutions_IdempotencyKey",
                table: "TaskRouterTriggerExecutions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterTriggerExecutions_TaskId_Event",
                table: "TaskRouterTriggerExecutions",
                columns: new[] { "TaskId", "Event" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRouterVariables_WorkflowRunId_TaskId_Name",
                table: "TaskRouterVariables",
                columns: new[] { "WorkflowRunId", "TaskId", "Name" },
                unique: true,
                filter: "[TaskId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_AssignedSectionId",
                table: "WorkItems",
                column: "AssignedSectionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_DocumentId_AssignedSectionId",
                table: "WorkItems",
                columns: new[] { "DocumentId", "AssignedSectionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropTable(
                name: "TaskRouterForkManifestEntries");

            migrationBuilder.DropTable(
                name: "TaskRouterOutbox");

            migrationBuilder.DropTable(
                name: "TaskRouterSubWorkflowAttachments");

            migrationBuilder.DropTable(
                name: "TaskRouterSubWorkflowInstances");

            migrationBuilder.DropTable(
                name: "TaskRouterTaskLogs");

            migrationBuilder.DropTable(
                name: "TaskRouterTaskOutcomes");

            migrationBuilder.DropTable(
                name: "TaskRouterTaskRoutes");

            migrationBuilder.DropTable(
                name: "TaskRouterTriggerDefinitions");

            migrationBuilder.DropTable(
                name: "TaskRouterTriggerExecutions");

            migrationBuilder.DropTable(
                name: "TaskRouterVariables");

            migrationBuilder.DropTable(
                name: "WorkItems");

            migrationBuilder.DropTable(
                name: "TaskRouterTasks");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropTable(
                name: "Sections");

            migrationBuilder.DropTable(
                name: "TaskRouterForkManifests");

            migrationBuilder.DropTable(
                name: "TaskRouterRuns");

            migrationBuilder.DropTable(
                name: "TaskRouterTaskDefinitions");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropTable(
                name: "Divisions");

            migrationBuilder.DropTable(
                name: "TaskRouterDefinitionVersions");

            migrationBuilder.DropTable(
                name: "TaskRouterTaskTypes");

            migrationBuilder.DropTable(
                name: "TaskRouterDefinitions");
        }
    }
}
