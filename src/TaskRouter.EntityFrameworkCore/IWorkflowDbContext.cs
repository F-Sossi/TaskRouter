using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// The surface the engine needs from the host's DbContext. The host's own context
/// implements this, so engine writes and host writes share one change tracker and
/// one transaction.
///
/// <para><b>A host writes nothing but the declaration:</b></para>
/// <code>
/// public partial class MyDbContext : DbContext, IWorkflowDbContext
/// {
///     protected override void OnModelCreating(ModelBuilder b) => b.ConfigureTaskRouter();
/// }
/// </code>
///
/// <para>The three members at the top — <c>Set&lt;T&gt;()</c>, <see cref="Database"/> and
/// <see cref="SaveChangesAsync"/> — are ones <c>DbContext</c> already declares with exactly
/// these signatures, so they are satisfied implicitly. Every set below is then defaulted in
/// terms of <c>Set&lt;T&gt;()</c>, which returns the context's own set: the engine reaches
/// the same tracked entities the host does, which is the whole reason it borrows the host's
/// context rather than opening one of its own.</para>
///
/// <para><b>Why defaults rather than requirements.</b> These were nineteen abstract
/// properties, and writing them out was the largest mechanical cost of adopting this
/// library. They carried a subtler cost too: a host whose context already had a member by
/// one of these names could not declare both implicitly, so it needed an explicit interface
/// implementation — after which <c>context.WorkflowTriggerDefinitions</c> and
/// <c>((IWorkflowDbContext)context).WorkflowTriggerDefinitions</c> meant different tables
/// while both compiled. A host may still declare any of these itself and its own member
/// wins; it is simply no longer forced to declare the other eighteen to do so.</para>
///
/// <para><b>Reach them through the interface.</b> A default interface member is visible
/// through the interface, not through the implementing class — so a host wanting
/// <c>WorkflowTasks</c> in its own code uses <c>Set&lt;WorkflowTask&gt;()</c> or casts. That
/// is a feature rather than a wart: it leaves exactly one meaning for any name the host
/// declares itself.</para>
/// </summary>
public interface IWorkflowDbContext
{
    // The three DbContext already supplies, which is why a host writes none of this.
    DbSet<TEntity> Set<TEntity>() where TEntity : class;
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    // ───────────────────────────── Definition side ─────────────────────────────

    DbSet<TaskTypeDefinition> WorkflowTaskTypes => Set<TaskTypeDefinition>();
    DbSet<OutcomeTypeDefinition> WorkflowOutcomeTypes => Set<OutcomeTypeDefinition>();
    DbSet<PreAssignment> WorkflowPreAssignments => Set<PreAssignment>();
    DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    DbSet<WorkflowDefinitionVersion> WorkflowDefinitionVersions => Set<WorkflowDefinitionVersion>();
    DbSet<WorkflowTaskDefinition> WorkflowTaskDefinitions => Set<WorkflowTaskDefinition>();
    DbSet<TaskOutcomeDefinition> WorkflowTaskOutcomes => Set<TaskOutcomeDefinition>();
    DbSet<TaskRoute> WorkflowTaskRoutes => Set<TaskRoute>();
    DbSet<TriggerDefinition> WorkflowTriggerDefinitions => Set<TriggerDefinition>();
    DbSet<SubWorkflowAttachment> WorkflowSubWorkflowAttachments => Set<SubWorkflowAttachment>();
    DbSet<SubWorkflowInstance> WorkflowSubWorkflowInstances => Set<SubWorkflowInstance>();

    // ─────────────────────────────── Runtime side ───────────────────────────────

    DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();
    DbSet<ForkManifest> WorkflowForkManifests => Set<ForkManifest>();
    DbSet<ForkManifestEntry> WorkflowForkManifestEntries => Set<ForkManifestEntry>();
    DbSet<WorkflowVariable> WorkflowVariables => Set<WorkflowVariable>();
    DbSet<WorkflowTaskLog> WorkflowTaskLogs => Set<WorkflowTaskLog>();
    DbSet<TriggerExecution> WorkflowTriggerExecutions => Set<TriggerExecution>();
    DbSet<WorkflowOutboxMessage> WorkflowOutbox => Set<WorkflowOutboxMessage>();
}
