using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;

using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;

namespace DemoDocuments.Server.Data;

/// <summary>
/// The host's DbContext, which also implements IWorkflowDbContext.
///
/// This is the integration pattern for the original system: the host's DbContext would implement
/// IWorkflowDbContext and call ConfigureTaskRouter() from OnModelCreating.
/// Engine tables then live in the host database and share the host's transaction,
/// which is what allows a task completion and a domain update to commit atomically.
/// </summary>
public class DemoDbContext(DbContextOptions<DemoDbContext> options)
    : DbContext(options), IWorkflowDbContext
{
    // ── Host domain ──
    public DbSet<DocumentBase> Documents => Set<DocumentBase>();
    public DbSet<ChangeRequest> ChangeRequests => Set<ChangeRequest>();
    public DbSet<Drawing> Drawings => Set<Drawing>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<Division> Divisions => Set<Division>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    // ── Engine (IWorkflowDbContext) ──
    public DbSet<TaskTypeDefinition> WorkflowTaskTypes => Set<TaskTypeDefinition>();
    public DbSet<OutcomeTypeDefinition> WorkflowOutcomeTypes => Set<OutcomeTypeDefinition>();
    public DbSet<PreAssignment> WorkflowPreAssignments => Set<PreAssignment>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowDefinitionVersion> WorkflowDefinitionVersions => Set<WorkflowDefinitionVersion>();
    public DbSet<WorkflowTaskDefinition> WorkflowTaskDefinitions => Set<WorkflowTaskDefinition>();
    public DbSet<TaskOutcomeDefinition> WorkflowTaskOutcomes => Set<TaskOutcomeDefinition>();
    public DbSet<TaskRoute> WorkflowTaskRoutes => Set<TaskRoute>();
    public DbSet<TriggerDefinition> WorkflowTriggerDefinitions => Set<TriggerDefinition>();
    public DbSet<SubWorkflowAttachment> WorkflowSubWorkflowAttachments => Set<SubWorkflowAttachment>();
    public DbSet<SubWorkflowInstance> WorkflowSubWorkflowInstances => Set<SubWorkflowInstance>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();
    public DbSet<ForkManifest> WorkflowForkManifests => Set<ForkManifest>();
    public DbSet<ForkManifestEntry> WorkflowForkManifestEntries => Set<ForkManifestEntry>();
    public DbSet<WorkflowVariable> WorkflowVariables => Set<WorkflowVariable>();
    public DbSet<WorkflowTaskLog> WorkflowTaskLogs => Set<WorkflowTaskLog>();
    public DbSet<TriggerExecution> WorkflowTriggerExecutions => Set<TriggerExecution>();
    public DbSet<WorkflowOutboxMessage> WorkflowOutbox => Set<WorkflowOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        // One call wires up every engine table, index and relationship.
        modelBuilder.ConfigureTaskRouter();

        // Table-per-hierarchy on a DocumentType discriminator, mirroring the original system.
        modelBuilder.Entity<DocumentBase>(e =>
        {
            e.ToTable("Documents");
            e.HasDiscriminator<string>("DocumentTypeDiscriminator")
             .HasValue<ChangeRequest>(nameof(DemoDocumentType.ChangeRequest))
             .HasValue<Drawing>(nameof(DemoDocumentType.DW));

            e.Property(x => x.Title).HasMaxLength(400).IsRequired();
            e.Property(x => x.DocNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.Revision).HasMaxLength(20);
            e.Ignore(x => x.DocumentType);      // computed from the concrete type

            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId);
            e.HasMany(x => x.WorkItems)
             .WithOne(x => x.Document!)
             .HasForeignKey(x => x.DocumentId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.DocNumber);
        });

        modelBuilder.Entity<WorkItem>(e =>
        {
            e.HasIndex(x => new { x.DocumentId, x.AssignedSectionId }).IsUnique();
            e.Property(x => x.CompletionPercentage).HasColumnName("CompletionPercentage");
            e.HasOne(x => x.AssignedSection).WithMany().HasForeignKey(x => x.AssignedSectionId);
        });

        modelBuilder.Entity<Group>(e => e.Property(x => x.Name).HasMaxLength(200));

        modelBuilder.Entity<Section>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(50);
            e.HasOne(x => x.Division).WithMany().HasForeignKey(x => x.DivisionId);
        });

        modelBuilder.Entity<Person>(e =>
        {
            e.HasKey(x => x.ActorId);
            e.Property(x => x.ActorId).HasMaxLength(200);
        });

    }
}
