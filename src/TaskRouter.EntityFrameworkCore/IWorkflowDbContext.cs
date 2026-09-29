using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// The surface the engine needs from the host's DbContext. The host's own context
/// implements this, so engine writes and host writes share one change tracker and
/// one transaction.
/// </summary>
public interface IWorkflowDbContext
{
    DbSet<TaskTypeDefinition> WorkflowTaskTypes { get; }
    DbSet<OutcomeTypeDefinition> WorkflowOutcomeTypes { get; }
    DbSet<PreAssignment> WorkflowPreAssignments { get; }
    DbSet<WorkflowDefinition> WorkflowDefinitions { get; }
    DbSet<WorkflowDefinitionVersion> WorkflowDefinitionVersions { get; }
    DbSet<WorkflowTaskDefinition> WorkflowTaskDefinitions { get; }
    DbSet<TaskOutcomeDefinition> WorkflowTaskOutcomes { get; }
    DbSet<TaskRoute> WorkflowTaskRoutes { get; }
    DbSet<TriggerDefinition> WorkflowTriggerDefinitions { get; }
    DbSet<SubWorkflowAttachment> WorkflowSubWorkflowAttachments { get; }
    DbSet<SubWorkflowInstance> WorkflowSubWorkflowInstances { get; }

    DbSet<WorkflowRun> WorkflowRuns { get; }
    DbSet<WorkflowTask> WorkflowTasks { get; }
    DbSet<ForkManifest> WorkflowForkManifests { get; }
    DbSet<ForkManifestEntry> WorkflowForkManifestEntries { get; }
    DbSet<WorkflowVariable> WorkflowVariables { get; }
    DbSet<WorkflowTaskLog> WorkflowTaskLogs { get; }
    DbSet<TriggerExecution> WorkflowTriggerExecutions { get; }
    DbSet<WorkflowOutboxMessage> WorkflowOutbox { get; }

    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
