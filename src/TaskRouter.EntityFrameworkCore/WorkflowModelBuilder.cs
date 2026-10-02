using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Entity configuration for the workflow engine. The host calls this from its own
/// DbContext's OnModelCreating, so workflow tables live in the host's database and
/// participate in the host's transactions — which is what makes atomic writes across
/// engine and domain data possible.
/// </summary>
public static class WorkflowModelBuilderExtensions
{
    public static ModelBuilder ConfigureTaskRouter(this ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);

        b.Entity<TaskTypeDefinition>(e =>
        {
            e.ToTable("TaskRouterTaskTypes");
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
        });

        b.Entity<WorkflowDefinition>(e =>
        {
            e.ToTable("TaskRouterDefinitions");
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasMany(x => x.Versions)
             .WithOne(x => x.WorkflowDefinition!)
             .HasForeignKey(x => x.WorkflowDefinitionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WorkflowDefinitionVersion>(e =>
        {
            e.ToTable("TaskRouterDefinitionVersions");
            e.Property(x => x.SubjectType).HasMaxLength(100);
            e.HasIndex(x => new { x.WorkflowDefinitionId, x.Version }).IsUnique();

            // Hosts resolve "which workflow starts for this subject type" through this.
            e.HasIndex(x => new { x.SubjectType, x.IsPublished, x.IsLatest });
            e.HasMany(x => x.Tasks)
             .WithOne(x => x.WorkflowDefinitionVersion!)
             .HasForeignKey(x => x.WorkflowDefinitionVersionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WorkflowTaskDefinition>(e =>
        {
            e.ToTable("TaskRouterTaskDefinitions");
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.AssignmentRoleKey).HasMaxLength(100);

            e.HasOne(x => x.TaskType)
             .WithMany()
             .HasForeignKey(x => x.TaskTypeDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.ValidOutcomes)
             .WithOne(x => x.TaskDefinition!)
             .HasForeignKey(x => x.TaskDefinitionId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(x => x.Triggers)
             .WithOne(x => x.TaskDefinition!)
             .HasForeignKey(x => x.TaskDefinitionId)
             .OnDelete(DeleteBehavior.Cascade);

            // Note: no unique index on (version, task type). Repeating a task type
            // within a workflow is legal now that routes target definition ids.
        });

        b.Entity<OutcomeTypeDefinition>(e =>
        {
            e.ToTable("TaskRouterOutcomeTypes");
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
        });

        b.Entity<TaskOutcomeDefinition>(e =>
        {
            e.ToTable("TaskRouterTaskOutcomes");
            e.Property(x => x.OutcomeKey).HasMaxLength(100);
            e.HasIndex(x => new { x.TaskDefinitionId, x.OutcomeKey }).IsUnique();
        });

        b.Entity<PreAssignment>(e =>
        {
            e.ToTable("TaskRouterPreAssignments");
            e.Property(x => x.ActorId).HasMaxLength(200);
            e.Property(x => x.BranchKey).HasMaxLength(200);

            // One standing answer per step per run. Setting it again replaces it, which is
            // what "go back in and change it" means.
            e.HasIndex(x => new { x.WorkflowRunId, x.TaskDefinitionId }).IsUnique();

            e.HasOne(x => x.Run)
             .WithMany()
             .HasForeignKey(x => x.WorkflowRunId)
             .OnDelete(DeleteBehavior.Cascade);

            // Restrict, unlike the run: a task definition belongs to a published version and
            // is never deleted while a run points at it.
            e.HasOne(x => x.TaskDefinition)
             .WithMany()
             .HasForeignKey(x => x.TaskDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TaskRoute>(e =>
        {
            e.ToTable("TaskRouterTaskRoutes");
            e.Property(x => x.ConditionKey).HasMaxLength(100);

            // Restrict, like every other relationship here. A cascade would let deleting an
            // outcome quietly delete the routes that fire on it, which is the workflow
            // changing shape as a side effect of tidying a picker.
            e.HasOne(x => x.Outcome)
             .WithMany()
             .HasForeignKey(x => x.TaskOutcomeDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.TaskDefinition)
             .WithMany(x => x.OutgoingRoutes)
             .HasForeignKey(x => x.TaskDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.NextTaskDefinition)
             .WithMany()
             .HasForeignKey(x => x.NextTaskDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TriggerDefinition>(e =>
        {
            e.ToTable("TaskRouterTriggerDefinitions");
            e.Property(x => x.TriggerKey).HasMaxLength(200);
            e.HasIndex(x => new { x.TaskDefinitionId, x.Event });
        });

        b.Entity<SubWorkflowAttachment>(e =>
        {
            e.ToTable("TaskRouterSubWorkflowAttachments");

            e.HasOne(x => x.SubWorkflowDefinition)
             .WithMany()
             .HasForeignKey(x => x.SubWorkflowDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);

            // Cascade: an attachment cannot outlive the version that scopes it.
            e.HasOne(x => x.DefinitionVersion)
             .WithMany(v => v.SubWorkflowAttachments)
             .HasForeignKey(x => x.WorkflowDefinitionVersionId)
             .OnDelete(DeleteBehavior.Cascade);

            // NoAction, not Cascade — and not because cascading would be wrong in
            // principle. WorkflowTaskDefinition already cascades from
            // WorkflowDefinitionVersion, so a second cascade here gives SQL Server two
            // paths from version to attachment and it refuses the schema outright
            // ("may cause cycles or multiple cascade paths"). The version FK above is
            // the one worth keeping, since it covers version-wide rows too.
            //
            // Nothing is lost: ClearGraphAsync deletes attachments explicitly before
            // task definitions, so the ordering that matters is enforced in code rather
            // than by the database.
            e.HasOne(x => x.TaskDefinition)
             .WithMany()
             .HasForeignKey(x => x.TaskDefinitionId)
             .OnDelete(DeleteBehavior.NoAction);

            // One attachment of a given sub-workflow per task per version, and — because
            // SQL Server treats NULLs as equal for uniqueness — exactly one version-wide
            // attachment per sub-workflow per version.
            //
            // HasFilter(null) is load-bearing. EF's SQL Server provider adds
            // "[TaskDefinitionId] IS NOT NULL" to any unique index over a nullable column,
            // which would put version-wide rows outside the index and allow duplicates of
            // exactly the row this is meant to make unique. Clearing the filter is what
            // brings the NULLs back under it.
            //
            // This makes the index SQL Server-specific, like the convergence index already
            // is: another provider may treat NULLs as distinct and enforce nothing here.
            e.HasIndex(x => new
            {
                x.WorkflowDefinitionVersionId,
                x.TaskDefinitionId,
                x.SubWorkflowDefinitionId
            }).IsUnique().HasFilter(null);
        });

        b.Entity<SubWorkflowInstance>(e =>
        {
            e.ToTable("TaskRouterSubWorkflowInstances");

            e.HasOne(x => x.DefinitionVersion)
             .WithMany()
             .HasForeignKey(x => x.SubWorkflowDefinitionVersionId)
             .OnDelete(DeleteBehavior.Restrict);

            // Restrict, not Cascade: deleting a task that spawned a sub-workflow would
            // silently take the instance and its history with it.
            e.HasOne(x => x.ParentTask)
             .WithMany()
             .HasForeignKey(x => x.ParentTaskId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.ParentTaskId);
            e.HasIndex(x => new { x.WorkflowRunId, x.Status });
        });

        b.Entity<WorkflowRun>(e =>
        {
            e.ToTable("TaskRouterRuns");

            // Subject is an owned value object: the engine stores the host's key and
            // discriminator without ever modelling the host's entity.
            e.OwnsOne(x => x.Subject, s =>
            {
                s.Property(p => p.SubjectType).HasColumnName("SubjectType").HasMaxLength(100).IsRequired();
                s.Property(p => p.SubjectId).HasColumnName("SubjectId").HasMaxLength(200).IsRequired();
                s.HasIndex(p => new { p.SubjectType, p.SubjectId });
            });

            e.HasOne(x => x.DefinitionVersion)
             .WithMany()
             .HasForeignKey(x => x.WorkflowDefinitionVersionId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.Tasks)
             .WithOne(x => x.Run!)
             .HasForeignKey(x => x.WorkflowRunId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WorkflowTask>(e =>
        {
            e.ToTable("TaskRouterTasks");
            e.Property(x => x.OutcomeKey).HasMaxLength(100);
            e.Property(x => x.AssignedToActorId).HasMaxLength(200);
            e.Property(x => x.AssignedBranchKey).HasMaxLength(200);

            e.HasOne(x => x.TaskDefinition)
             .WithMany()
             .HasForeignKey(x => x.TaskDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.ParentTask)
             .WithMany()
             .HasForeignKey(x => x.ParentTaskId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.ForkManifest)
             .WithMany()
             .HasForeignKey(x => x.ForkManifestId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.WorkflowRunId);
            e.HasIndex(x => x.ForkGroupId);
            e.HasIndex(x => x.ParentTaskId);
            e.HasIndex(x => x.SubWorkflowInstanceId);

            // Assigned-work lookups ("my open tasks") are the most common query in a
            // task-driven system, so index for them explicitly.
            //
            // Filtered to the open statuses for the same reason as the two sweeper indexes
            // below: the inbox only ever asks for NotStarted or InProgress, and a task that
            // completes leaves the set permanently. Unfiltered, this index grew with every
            // task the system had ever run while the query only wanted the live head of it
            // -- so its size tracked total history rather than outstanding work.
            //
            // The ordinals are spelled out because a filter cannot name the enum.
            // SchemaGuardTests pins them; reordering WorkflowTaskStatus without updating
            // both would leave the index in place and silently change what it covers,
            // which shows up as an inbox that quietly stops using it.
            e.HasIndex(x => new { x.AssignedToActorId, x.Status })
             .HasFilter("[Status] IN (0, 1)");

            // The sweeper's candidate set: open, not yet reminded, configured to nudge.
            // Filtered on ReminderSentAt because the interesting rows are the ones that
            // have not fired, and every task that ever fires leaves the set permanently.
            // Without the filter this index grows with the table forever while the query
            // only ever wants its shrinking head.
            e.HasIndex(x => new { x.Status, x.ReminderSentAt })
             .HasFilter("[ReminderSentAt] IS NULL");

            // The overdue sweep's candidate set. DueDate is in the key, unlike the
            // reminder index above, because that query does filter on it — see the
            // asymmetry argued out in DeadlineProcessor.OverdueCandidatesAsync. Same
            // filtered shape for the same reason: a task that fires leaves the set for
            // good, so the index only ever needs its shrinking head.
            e.HasIndex(x => new { x.Status, x.OverdueFiredAt, x.DueDate })
             .HasFilter("[OverdueFiredAt] IS NULL");

            // Convergence guard. The original engine's review finding C2: convergence was a
            // check-then-act with no locking, so concurrent branch completions could
            // create two convergence tasks. A filtered unique index makes the second
            // insert fail loudly instead of corrupting the run.
            e.HasIndex(x => x.ForkManifestId)
             .IsUnique()
             .HasFilter("[ForkManifestId] IS NOT NULL AND [Status] <> 3");
        });

        b.Entity<ForkManifest>(e =>
        {
            e.ToTable("TaskRouterForkManifests");
            e.HasIndex(x => x.ForkGroupId).IsUnique();
            e.HasMany(x => x.Entries)
             .WithOne(x => x.ForkManifest!)
             .HasForeignKey(x => x.ForkManifestId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ForkManifestEntry>(e =>
        {
            e.ToTable("TaskRouterForkManifestEntries");
            e.Property(x => x.BranchKey).HasMaxLength(200);
            e.HasIndex(x => new { x.ForkManifestId, x.BranchKey }).IsUnique();
        });

        b.Entity<WorkflowVariable>(e =>
        {
            e.ToTable("TaskRouterVariables");
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.WorkflowRunId, x.TaskId, x.Name }).IsUnique();
        });

        b.Entity<WorkflowTaskLog>(e =>
        {
            e.ToTable("TaskRouterTaskLogs");
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.PerformedBy).HasMaxLength(200);
            e.HasOne(x => x.Task)
             .WithMany()
             .HasForeignKey(x => x.TaskId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TaskId);
        });

        b.Entity<TriggerExecution>(e =>
        {
            e.ToTable("TaskRouterTriggerExecutions");
            e.Property(x => x.IdempotencyKey).HasMaxLength(300);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.TaskId, x.Event });
        });

        b.Entity<WorkflowOutboxMessage>(e =>
        {
            e.ToTable("TaskRouterOutbox");
            e.Property(x => x.IdempotencyKey).HasMaxLength(300);
            e.Property(x => x.ActorId).HasMaxLength(200);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();

            // The claim query: pending work that is due, oldest first.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });

        return b;
    }
}
