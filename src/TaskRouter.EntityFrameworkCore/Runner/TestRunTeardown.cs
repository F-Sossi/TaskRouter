using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Runner;

/// <summary>
/// Hard-deletes a test run and everything hanging off it.
///
/// Shared because two callers need it, and only one of them is obvious. The runner
/// deletes a test run when somebody discards it. The <em>builder</em> has to delete one
/// too: re-saving a draft rewrites its graph from scratch, and a test run's tasks point
/// at the task definitions that rewrite destroys — so trying a draft out would otherwise
/// make it unsaveable and unpublishable.
///
/// Callers own the decision. This type asks no questions — in particular it does not
/// check <see cref="WorkflowRun.IsTest"/>, because the guard belongs where the intent is
/// known. Never hand it a run somebody did real work in.
/// </summary>
internal static class TestRunTeardown
{
    internal static async Task DiscardAsync(
        IWorkflowDbContext db, WorkflowRun run, CancellationToken ct)
    {
        var runId = run.Id;

        var taskIds = await db.WorkflowTasks
            .Where(t => t.WorkflowRunId == runId).Select(t => t.Id).ToListAsync(ct).ConfigureAwait(false);

        db.WorkflowTaskLogs.RemoveRange(
            await db.WorkflowTaskLogs.Where(l => taskIds.Contains(l.TaskId))
                .ToListAsync(ct).ConfigureAwait(false));

        db.WorkflowTriggerExecutions.RemoveRange(
            await db.WorkflowTriggerExecutions.Where(x => taskIds.Contains(x.TaskId))
                .ToListAsync(ct).ConfigureAwait(false));

        db.WorkflowVariables.RemoveRange(
            await db.WorkflowVariables.Where(v => v.WorkflowRunId == runId)
                .ToListAsync(ct).ConfigureAwait(false));

        // Delegated chains and forks, which the run's cascade cannot reach.
        //
        // A sub-workflow instance points at the task it hangs off, and that relationship is
        // Restrict on purpose -- a task with live delegated work under it must not vanish.
        // Discarding a test run is the one case where the whole tree is meant to go, so it
        // has to take these out itself. Until it did, the first delegation tried in the
        // builder's test pane left that run undeletable, and the only signal was a 500.
        db.WorkflowSubWorkflowInstances.RemoveRange(
            await db.WorkflowSubWorkflowInstances.Where(i => i.WorkflowRunId == runId)
                .ToListAsync(ct).ConfigureAwait(false));

        // Manifest entries cascade from the manifest; the manifests themselves are reached
        // through the run. A convergence task references its manifest with Restrict, so the
        // manifests have to outlive the tasks -- which they do, because the tasks go with
        // the run in the second save below.
        var manifests = await db.WorkflowForkManifests
            .Where(m => m.WorkflowRunId == runId).ToListAsync(ct).ConfigureAwait(false);

        // Two saves, and the split is load-bearing.
        //
        // The tasks are never tracked here -- only their ids were selected -- so EF does not
        // know that run -> task -> log is a chain, and is free to order the run's DELETE
        // before the logs'. The database's cascade then removes those log rows itself, and
        // EF's own DELETE for each affects zero rows, which it reports as a concurrency
        // conflict. Saving the children first removes the ambiguity rather than relying on
        // an ordering EF never promised.
        //
        // This survived in the demo only by accident: there the client shared one DbContext
        // with everything else the test did, so the tasks happened to be tracked and the
        // chain happened to be visible. Over HTTP each request is its own scope and nothing
        // is tracked, which is what exposed it.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        db.WorkflowRuns.Remove(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Last, because a task referencing a manifest is Restrict and those tasks only went
        // with the run a moment ago.
        if (manifests.Count > 0)
        {
            db.WorkflowForkManifests.RemoveRange(manifests);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
