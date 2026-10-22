using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.EntityFrameworkCore;

public sealed partial class WorkflowEngine
{
    /// <summary>
    /// What a completion would set off, as the confirmation dialog lists it: the completed
    /// step's own triggers, then each step it creates, any sub-workflow those start, and
    /// whatever closes itself as a result -- a terminal step that completes automatically,
    /// a sub-workflow instance with nothing left open, the run.
    ///
    /// <para>Played out in memory over a no-tracking copy of the run, mirroring the order
    /// <see cref="FinishAsync"/> and <see cref="CompleteAutomaticallyAsync"/> act in. Nothing
    /// is saved and no trigger runs, except built-in in-transaction Set Variable, which
    /// writes to a <see cref="PreviewVariables"/> so a later step's condition sees the value
    /// it would. Host triggers that change data or call out cannot be simulated; they
    /// describe themselves and that is all.</para>
    /// </summary>
    private async Task<IReadOnlyList<PlannedAction>> PreviewCascadeAsync(
        WorkflowTask source, List<(WorkflowTask Task, bool Converges)> next,
        string actorId, BranchContext? rejection, CancellationToken ct)
    {
        var run = await db.WorkflowRuns.AsNoTracking().Include(run => run.DefinitionVersion)
            .SingleAsync(run => run.Id == source.WorkflowRunId, ct).ConfigureAwait(false);
        var tasks = await db.WorkflowTasks.AsNoTracking().Include(task => task.TaskDefinition!).ThenInclude(definition => definition.TaskType)
            .Where(task => task.WorkflowRunId == source.WorkflowRunId).ToListAsync(ct).ConfigureAwait(false);
        tasks.RemoveAll(task => task.Id == source.Id);
        tasks.Add(source);
        var instances = await db.WorkflowSubWorkflowInstances.AsNoTracking()
            .Where(instance => instance.WorkflowRunId == run.Id).ToListAsync(ct).ConfigureAwait(false);
        var storedVariables = await db.WorkflowVariables.AsNoTracking().Where(variable => variable.WorkflowRunId == run.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var runVariables = storedVariables.Where(variable => variable.TaskId is null)
            .ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.OrdinalIgnoreCase);
        var taskVariables = new Dictionary<WorkflowTask, Dictionary<string, string?>>();
        var automatic = new Queue<WorkflowTask>();
        var actions = new List<PlannedAction>();
        var spawnPath = new HashSet<int>();
        var plannedParents = new Dictionary<WorkflowTask, WorkflowTask>();
        var instanceParents = new Dictionary<int, WorkflowTask>();
        var plannedInstanceId = -1;

        async Task DescribeAsync(WorkflowTask task, WorkflowEventKind eventKind,
            IReadOnlyList<WorkflowTaskSnapshot> following, BranchContext? branch = null)
        {
            if (!taskVariables.TryGetValue(task, out var variables))
            {
                variables = storedVariables.Where(variable => task.Id != 0 && variable.TaskId == task.Id)
                    .ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.OrdinalIgnoreCase);
                taskVariables[task] = variables;
            }

            var preview = new TriggerPreview(following)
            {
                Run = run.ToSnapshot(tasks),
                Variables = new PreviewVariables(runVariables, variables)
            };
            actions.AddRange(await dispatcher!.PreviewAsync(task, eventKind, actorId, preview, branch, ct).ConfigureAwait(false));
        }

        async Task ReachedAsync(WorkflowTask task, bool converges, bool subWorkflow = false)
        {
            if (!tasks.Contains(task)) tasks.Add(task);
            if (task.TaskDefinition is { IsTerminal: true, CompletesAutomatically: true }) automatic.Enqueue(task);
            if (subWorkflow) await DescribeAsync(task, WorkflowEventKind.SubWorkflowStarted, []);
            if (converges)
                await DescribeAsync(task, WorkflowEventKind.ForkConverged, [], await ConvergedBranchAsync(task, ct).ConfigureAwait(false));
            var branch = task.ForkGroupId is null ? null
                : new BranchContext(task.ForkGroupId, task.ForkManifestId, task.AssignedBranchKey, []);
            await DescribeAsync(task, WorkflowEventKind.TaskCreated, [], branch);

            if (!spawnPath.Add(task.TaskDefinitionId))
                throw new InvalidOperationException("Automatic sub-workflow attachments contain a cycle.");
            try
            {
                var attachments = await db.WorkflowSubWorkflowAttachments.AsNoTracking()
                    .Where(attachment => attachment.TaskDefinitionId == task.TaskDefinitionId && attachment.IsAutomatic && !attachment.IsArchived)
                    .OrderBy(attachment => attachment.Id).ToListAsync(ct).ConfigureAwait(false);
                foreach (var attachment in attachments)
                {
                    var version = await db.WorkflowDefinitionVersions.AsNoTracking().Include(version => version.Tasks).ThenInclude(definition => definition.TaskType)
                        .SingleAsync(version => version.WorkflowDefinitionId == attachment.SubWorkflowDefinitionId
                            && version.IsPublished && version.IsLatest && !version.IsArchived, ct).ConfigureAwait(false);
                    var definition = version.Tasks.Single(definition => definition.Id == version.EntryTaskDefinitionId && !definition.IsArchived);
                    var assignment = await ResolveAssignmentAsync(definition,
                        new(task.AssignedToActorId, task.AssignedBranchKey), task.ToSnapshot(), AssignmentOrigin.Inherited, ct).ConfigureAwait(false);
                    var instance = new SubWorkflowInstance
                    {
                        Id = plannedInstanceId--,
                        WorkflowRunId = run.Id,
                        ParentTaskId = task.Id,
                        SubWorkflowDefinitionVersionId = version.Id,
                        IsBlocking = attachment.IsBlocking,
                        Status = SubWorkflowStatus.Running
                    };
                    instances.Add(instance);
                    instanceParents[instance.Id] = task;
                    var child = Planned(task, definition, assignment, DateTime.UtcNow);
                    child.ParentTaskId = task.Id;
                    child.SubWorkflowInstanceId = instance.Id;
                    plannedParents[child] = task;
                    await ReachedAsync(child, false, true);
                }
            }
            finally
            {
                spawnPath.Remove(task.TaskDefinitionId);
            }
        }

        async Task CloseAsync(WorkflowTask task)
        {
            if (task.SubWorkflowInstanceId is { } instanceId
                && instances.FirstOrDefault(instance => instance.Id == instanceId) is { Status: SubWorkflowStatus.Running } instance
                && !tasks.Any(candidate => candidate.SubWorkflowInstanceId == instanceId && IsOpen(candidate)))
            {
                instance.Status = SubWorkflowStatus.Completed;
                var parent = instanceParents.GetValueOrDefault(instanceId)
                    ?? tasks.FirstOrDefault(candidate => candidate.Id != 0 && candidate.Id == instance.ParentTaskId);
                if (parent is not null)
                {
                    await DescribeAsync(parent, WorkflowEventKind.SubWorkflowCompleted, []);
                    if (parent.TaskDefinition is { IsTerminal: true, CompletesAutomatically: true }) automatic.Enqueue(parent);
                }
            }

            var parentTask = plannedParents.GetValueOrDefault(task)
                ?? tasks.FirstOrDefault(candidate => candidate.Id != 0 && candidate.Id == task.ParentTaskId);
            if (parentTask is { TaskDefinition: { IsTerminal: true, CompletesAutomatically: true } } automaticParent)
                automatic.Enqueue(automaticParent);

            if (run.Status == WorkflowRunStatus.Running && !tasks.Any(IsOpen))
            {
                run.Status = WorkflowRunStatus.Completed;
                await DescribeAsync(task, WorkflowEventKind.RunCompleted, []);
            }
        }

        if (rejection is null)
            foreach (var step in next) await ReachedAsync(step.Task, step.Converges);
        else tasks.AddRange(next.Select(step => step.Task));
        var branch = rejection ?? (source.ForkGroupId is null ? null
            : new BranchContext(source.ForkGroupId, source.ForkManifestId, source.AssignedBranchKey, []));
        var following = next.Select(step => step.Task.ToSnapshot()).ToList();
        if (rejection is not null) await DescribeAsync(source, WorkflowEventKind.SelectiveRejection, following, rejection);
        await DescribeAsync(source, WorkflowEventKind.TaskCompleted, following, branch);
        if (source.ForkGroupId is not null) await DescribeAsync(source, WorkflowEventKind.BranchCompleted, following, branch);
        if (rejection is not null)
            foreach (var step in next) await ReachedAsync(step.Task, step.Converges);
        await CloseAsync(source);

        while (automatic.TryDequeue(out var task))
        {
            if (!IsOpen(task)
                || tasks.Any(candidate => (task.Id != 0 && candidate.ParentTaskId == task.Id || plannedParents.GetValueOrDefault(candidate) == task)
                    && candidate.TaskDefinition?.IsBlocking == true && IsOpen(candidate))
                || instances.Any(instance => (task.Id != 0 && instance.ParentTaskId == task.Id || instanceParents.GetValueOrDefault(instance.Id) == task)
                    && instance.IsBlocking && instance.Status == SubWorkflowStatus.Running)) continue;
            task.Status = WorkflowTaskStatus.Completed;
            task.AssignedToActorId = actorId;
            task.OutcomeKey = await db.WorkflowTaskOutcomes.Where(outcome => outcome.TaskDefinitionId == task.TaskDefinitionId && !outcome.IsArchived)
                .OrderBy(outcome => outcome.Order).ThenBy(outcome => outcome.Id).Select(outcome => outcome.OutcomeKey)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? DefaultOutcomeKey;
            task.CompletedDate = DateTime.UtcNow;
            await DescribeAsync(task, WorkflowEventKind.TaskCompleted, []);
            if (task.ForkGroupId is not null)
                await DescribeAsync(task, WorkflowEventKind.BranchCompleted, [], new(task.ForkGroupId, task.ForkManifestId, task.AssignedBranchKey, []));
            await CloseAsync(task);
        }

        return actions;
    }

    private static bool IsOpen(WorkflowTask task) => task.Status is WorkflowTaskStatus.NotStarted or WorkflowTaskStatus.InProgress;
}
