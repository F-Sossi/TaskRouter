using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// The inbox predicate.
///
/// This is the one piece of the inbox that must live in the library: every host asking
/// "what is waiting on me?" has to exclude forked ghosts and the builder's test runs,
/// and the hand-written version this replaces forgot the second one. Each test here is
/// a way a host writing the query itself would get it wrong.
/// </summary>
[TestClass]
public class InboxTests
{
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Starts a run and hands back its single entry task, so a test can put the
    /// assignment it cares about on a real task rather than a hand-built row.
    /// </summary>
    private async Task<WorkflowTask> StartAndGetEntryAsync(string subjectId)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", subjectId), _definitionId, "user-originator")).Unwrap();

        return await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);
    }

    [TestMethod]
    public async Task It_returns_a_task_assigned_to_the_actor()
    {
        var task = await StartAndGetEntryAsync("1");

        // StartRunAsync assigns the entry task to the starting actor with no branch key.
        Assert.AreEqual("user-originator", task.AssignedToActorId);

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", [])).Unwrap();

        var row = rows.Single(r => r.TaskId == task.Id);
        Assert.AreEqual("Enter Record", row.Label);
        Assert.AreEqual("enter-record", row.TaskTypeKey);
        Assert.AreEqual("ChangeRequest", row.Subject.SubjectType);
        Assert.AreEqual("1", row.Subject.SubjectId);
        Assert.AreEqual("ChangeRequest Document Review", row.WorkflowName);
        Assert.IsFalse(row.IsUnclaimed);
    }

    [TestMethod]
    public async Task It_does_not_return_a_task_assigned_to_somebody_else()
    {
        var task = await StartAndGetEntryAsync("1");

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-worker-c200", [])).Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_returns_unclaimed_work_in_one_of_the_actors_units()
    {
        var task = await StartAndGetEntryAsync("1");

        // The state DemoAssignmentResolver produces when a section has nobody in the
        // role: a branch key, and no owner.
        task.AssignedToActorId = null;
        task.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-worker-c200", ["C200"])).Unwrap();

        var row = rows.Single(r => r.TaskId == task.Id);
        Assert.IsTrue(row.IsUnclaimed);
        Assert.AreEqual("C200", row.AssignedBranchKey);
    }

    [TestMethod]
    public async Task It_does_not_return_unclaimed_work_in_another_unit()
    {
        var task = await StartAndGetEntryAsync("1");

        task.AssignedToActorId = null;
        task.AssignedBranchKey = "C300";
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-worker-c200", ["C200"])).Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_does_not_return_somebody_elses_task_in_the_actors_own_unit()
    {
        var task = await StartAndGetEntryAsync("1");

        task.AssignedToActorId = "user-lead-c200";
        task.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        // Unclaimed means unowned, not "owned by a colleague". An inbox that showed
        // a section's whole workload would be a supervisor view, not an inbox.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-worker-c200", ["C200"])).Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }

    // One DataRow per status so a regression in Cancelled or Forked is reported by name,
    // rather than stopping at Completed and leaving the other two unverified.
    [TestMethod]
    [DataRow(WorkflowTaskStatus.Completed)]
    [DataRow(WorkflowTaskStatus.Cancelled)]
    [DataRow(WorkflowTaskStatus.Forked)]
    public async Task It_excludes_completed_cancelled_and_forked_tasks(WorkflowTaskStatus status)
    {
        var task = await StartAndGetEntryAsync($"status-{status}");
        task.Status = status;
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", []))
            .Unwrap();

        Assert.IsFalse(
            rows.Any(r => r.TaskId == task.Id),
            $"A {status} task must not appear in an inbox.");
    }

    [TestMethod]
    public async Task It_excludes_tasks_in_a_test_run()
    {
        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == _definitionId && v.IsPublished && v.IsLatest)
            .Select(v => v.Id)
            .SingleAsync();

        var testRun = (await _host.Engine.StartRunOnVersionAsync(
            new WorkflowSubject("WorkflowTest", "1"), versionId, "user-originator",
            isTest: true)).Unwrap();

        // The regression this whole feature exists to prevent: an admin trying a draft
        // out must not put tasks in real people's inboxes.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", []))
            .Unwrap();

        Assert.IsFalse(rows.Any(r => r.WorkflowRunId == testRun.Id));
    }

    [TestMethod]
    public async Task It_excludes_archived_tasks()
    {
        var task = await StartAndGetEntryAsync("1");
        task.IsArchived = true;
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", []))
            .Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_excludes_unclaimed_work_when_no_units_are_given()
    {
        var task = await StartAndGetEntryAsync("1");

        task.AssignedToActorId = null;
        task.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        // Pins the empty-list short-circuit that the query comment describes: with no
        // units to match, the unclaimed clause must drop out of the generated SQL
        // rather than (say) matching NULL against an empty IN (...) in some EF version.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-worker-c200", []))
            .Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_matches_unclaimed_work_across_several_units()
    {
        var taskC100 = await StartAndGetEntryAsync("multi-unit-1");
        taskC100.AssignedToActorId = null;
        taskC100.AssignedBranchKey = "C100";

        var taskC300 = await StartAndGetEntryAsync("multi-unit-2");
        taskC300.AssignedToActorId = null;
        taskC300.AssignedBranchKey = "C300";

        await _host.Db.SaveChangesAsync();

        // The reason branchKeys is a list at all: an actor typically belongs to more
        // than one unit, and unclaimed work in any of them belongs in their inbox.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-worker-c200", ["C100", "C300"])).Unwrap();

        Assert.IsTrue(rows.Any(r => r.TaskId == taskC100.Id));
        Assert.IsTrue(rows.Any(r => r.TaskId == taskC300.Id));
    }

    [TestMethod]
    public async Task It_excludes_a_task_with_neither_an_actor_nor_a_unit()
    {
        var task = await StartAndGetEntryAsync("1");

        task.AssignedToActorId = null;
        task.AssignedBranchKey = null;
        await _host.Db.SaveChangesAsync();

        // A documented known limit of the design, not an accident: a task with no actor
        // and no branch key is in nobody's inbox until something assigns it.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-worker-c200", ["C200"])).Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_ignores_blank_and_duplicate_unit_keys()
    {
        var task = await StartAndGetEntryAsync("1");

        task.AssignedToActorId = null;
        task.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        // Exercises the Where(...)/Distinct() filtering directly: a duplicate "C200" and
        // blank entries must not change the result, only (in principle) the size of the
        // generated IN (...) list.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-worker-c200", ["C200", "C200", "", "  "])).Unwrap();

        Assert.AreEqual(1, rows.Count(r => r.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_falls_back_to_the_task_type_name_when_a_definition_has_no_display_name()
    {
        var task = await StartAndGetEntryAsync("1");

        var definition = await _host.Db.WorkflowTaskDefinitions
            .Include(d => d.TaskType)
            .SingleAsync(d => d.Id == task.TaskDefinitionId);

        // The seeder gives the definition and its task type the same display name
        // ("Enter Record"), so nulling one of them proves nothing on its own — the
        // assertion would hold whether the fallback fired or not. Renaming the type
        // first is what makes this test able to fail for the reason it exists.
        definition.TaskType!.DisplayName = "Fallback Type Name";
        definition.DisplayName = null;
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", []))
            .Unwrap();

        var row = rows.Single(r => r.TaskId == task.Id);

        // A null task-definition DisplayName must fall through to the task type's, via
        // the COALESCE-style rule in WorkflowEngine.Inbox.cs.
        Assert.AreEqual("Fallback Type Name", row.Label);
    }

    [TestMethod]
    public async Task It_fails_when_the_actor_id_is_blank()
    {
        // Try.RunAsync converts the ArgumentException from the guard into a failed
        // Result, so a blank actor id is a Result failure to the caller, not a throw.
        var result = await _host.Engine.GetOpenTasksForActorAsync("", []);

        Assert.IsTrue(result.IsError);
    }

    // ─────────────────────── Delegated and blocked work ───────────────────────

    /// <summary>
    /// Drives the seeded run to Provide Input, which is what the "Technical Review"
    /// sub-workflow is attached to, and returns that task's id.
    /// </summary>
    private async Task<int> DriveToProvideInputAsync(string subjectId)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", subjectId), _definitionId, "user-originator")).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, "approved", "user-originator")).Unwrap();

        return await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .SingleAsync();
    }

    [TestMethod]
    public async Task It_includes_the_tasks_of_a_delegated_sub_workflow()
    {
        var parentTaskId = await DriveToProvideInputAsync("1");

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator",
            assignment: new TaskRouter.Core.Abstractions.WorkflowAssignment(
                "user-worker-c200", "C200"))).Unwrap();

        // Delegated work is real work. A sub-workflow's tasks are ordinary tasks in the
        // parent's run, so the inbox needs no knowledge of sub-workflows to show them.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-worker-c200", ["C200"]))
            .Unwrap();

        var row = rows.Single(r => r.SubWorkflowInstanceId == instance.Id);
        Assert.AreEqual("Technical Review — Get Info", row.Label);
        Assert.IsFalse(row.IsBlocked, "The delegated task is the work, not the thing waiting.");
    }

    [TestMethod]
    public async Task It_flags_a_task_held_by_a_blocking_sub_workflow()
    {
        var parentTaskId = await DriveToProvideInputAsync("1");

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator",
            assignment: new TaskRouter.Core.Abstractions.WorkflowAssignment(
                "user-worker-c200", "C200"))).Unwrap();

        var parent = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == parentTaskId);
        var owner = parent.AssignedToActorId!;
        var unit = parent.AssignedBranchKey;

        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            owner, unit is null ? [] : [unit])).Unwrap();

        var row = rows.Single(r => r.TaskId == parentTaskId);

        // Flagged, not filtered: it is still this person's task, and it is still waiting.
        Assert.IsTrue(row.IsBlocked);
    }

    [TestMethod]
    public async Task It_does_not_flag_a_task_held_by_a_non_blocking_sub_workflow()
    {
        var parentTaskId = await DriveToProvideInputAsync("1");

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator",
            assignment: new TaskRouter.Core.Abstractions.WorkflowAssignment(
                "user-worker-c200", "C200"))).Unwrap();

        // IsBlocking is copied onto the instance from the attachment at spawn time
        // (SubWorkflowInstance.IsBlocking doc comment, Entities.cs), so flipping it
        // directly on the instance is faithful to how the engine actually reads it —
        // the seeded "Technical Review" attachment is blocking, so this is the only way
        // to get a non-blocking instance without reseeding.
        var stored = await _host.Db.WorkflowSubWorkflowInstances.SingleAsync(i => i.Id == instance.Id);
        stored.IsBlocking = false;
        await _host.Db.SaveChangesAsync();

        var parent = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == parentTaskId);
        var owner = parent.AssignedToActorId!;
        var unit = parent.AssignedBranchKey;

        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            owner, unit is null ? [] : [unit])).Unwrap();

        var row = rows.Single(r => r.TaskId == parentTaskId);

        Assert.IsFalse(row.IsBlocked);
    }

    [TestMethod]
    public async Task It_stops_flagging_a_task_once_the_sub_workflow_finishes()
    {
        var parentTaskId = await DriveToProvideInputAsync("1");

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator",
            assignment: new TaskRouter.Core.Abstractions.WorkflowAssignment(
                "user-worker-c200", "C200"))).Unwrap();

        var parent = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == parentTaskId);
        var owner = parent.AssignedToActorId!;
        var unit = parent.AssignedBranchKey;
        IReadOnlyList<string> units = unit is null ? [] : [unit];

        var before = (await _host.Engine.GetOpenTasksForActorAsync(owner, units)).Unwrap();
        Assert.IsTrue(
            before.Single(r => r.TaskId == parentTaskId).IsBlocked,
            "Setup assumption: the instance must be blocking before it finishes.");

        // Drives every open task in the instance to completion, the same way
        // SubWorkflowTests.DriveInstanceToCompletionAsync does, so the instance reaches
        // Completed on its own rather than being force-set.
        for (var guard = 0; guard < 10; guard++)
        {
            var open = await _host.Db.WorkflowTasks
                .Where(t => t.SubWorkflowInstanceId == instance.Id
                         && (t.Status == WorkflowTaskStatus.NotStarted
                          || t.Status == WorkflowTaskStatus.InProgress))
                .Select(t => t.Id)
                .ToListAsync();

            if (open.Count == 0)
            {
                break;
            }

            foreach (var id in open)
            {
                (await _host.Engine.CompleteTaskAsync(id, "approved", "user-worker-c200")).Unwrap();
            }
        }

        var finished = await _host.Db.WorkflowSubWorkflowInstances.SingleAsync(i => i.Id == instance.Id);
        Assert.AreEqual(SubWorkflowStatus.Completed, finished.Status, "Setup assumption: the instance must actually finish.");

        var after = (await _host.Engine.GetOpenTasksForActorAsync(owner, units)).Unwrap();

        // The transition users actually notice: a task that was blocked stops being so
        // once the thing blocking it is done.
        Assert.IsFalse(after.Single(r => r.TaskId == parentTaskId).IsBlocked);
    }

    [TestMethod]
    public async Task The_due_date_survives_the_projection()
    {
        // The projection is hand-written .Select, so a new column reaches
        // InboxTaskSnapshot only if somebody adds it in two places. This is the test that
        // notices.
        var task = await StartAndGetEntryAsync("1");

        var due = new DateTime(2026, 11, 3, 9, 30, 0, DateTimeKind.Utc);
        task.DueDate = due;
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", [])).Unwrap();

        Assert.AreEqual(due, rows.Single(r => r.TaskId == task.Id).DueDate);
    }

    [TestMethod]
    public async Task A_task_with_no_due_date_projects_null()
    {
        var task = await StartAndGetEntryAsync("1");

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", [])).Unwrap();

        Assert.IsNull(rows.Single(r => r.TaskId == task.Id).DueDate);
    }
}
