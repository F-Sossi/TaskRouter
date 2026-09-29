using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// The sub-workflow lifecycle.
///
/// The payoff for definition-id routing: the seeded "Technical Review" sub-workflow is
/// built from the same task types the mainline uses, and both can be in flight at once
/// without colliding. the original system could not express that, because its routes targeted a task
/// *type* that could appear only once per workflow.
///
/// There is no second engine. A sub-workflow's tasks are ordinary tasks in the parent's
/// run, so what these tests are really checking is that the beginning and the end are
/// right — the instance is created and pinned, it closes when its work runs out, and it
/// holds up a blocking parent until it does.
/// </summary>
[TestClass]
public class SubWorkflowTests
{
    private TestHost _host = null!;
    private int _runId;
    private int _parentTaskId;
    private int _subWorkflowId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();

        _subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        _runId = run.Id;

        // Drive to Provide Input, which is what the sub-workflow is attached to.
        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, "approved", "user-originator")).Unwrap();

        _parentTaskId = await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == _runId && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .SingleAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    // ─────────────────────────────── Spawning ───────────────────────────────

    [TestMethod]
    public async Task A_sub_workflow_starts_as_tasks_inside_the_parent_run()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator", notes: "please review")).Unwrap();

        Assert.AreEqual(SubWorkflowStatus.Running, instance.Status);
        Assert.AreEqual("Technical Review", instance.Name);
        Assert.AreEqual(1, instance.Version);
        Assert.AreEqual(_parentTaskId, instance.ParentTaskId);
        Assert.AreEqual(_runId, instance.WorkflowRunId);
        Assert.IsTrue(instance.IsBlocking);

        var entry = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .SingleAsync(t => t.SubWorkflowInstanceId == instance.Id);

        // Same run, not a separate one: the runner shows it without knowing sub-workflows
        // exist, and the whole thing stays one transaction and one history.
        Assert.AreEqual(_runId, entry.WorkflowRunId);
        Assert.AreEqual(_parentTaskId, entry.ParentTaskId);
        Assert.AreEqual("Technical Review — Get Info", entry.TaskDefinition!.DisplayName);
        Assert.AreEqual("please review", entry.Notes);
    }

    [TestMethod]
    public async Task A_task_type_can_be_in_the_mainline_and_a_sub_workflow_at_once()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        // The chain opens on Get Info; the Section Lead review is what it routes to.
        var entryId = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId == instance.Id)
            .Select(t => t.Id)
            .SingleAsync();

        (await _host.Engine.CompleteTaskAsync(entryId, "approved", "user-worker-c100")).Unwrap();

        var subTaskTypes = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .Where(t => t.SubWorkflowInstanceId == instance.Id)
            .Select(t => t.TaskDefinition!.TaskType!.Key)
            .ToListAsync();

        CollectionAssert.Contains(subTaskTypes, "section-review");

        // The mainline has ad-hoc definitions of the same type. Two definitions, one
        // type, distinct routes — which is only legal because routes target definitions.
        var mainlineSameType = await _host.Db.WorkflowTaskDefinitions
            .Include(d => d.TaskType)
            .CountAsync(d => d.TaskType!.Key == "section-review");

        Assert.IsGreaterThan(1, mainlineSameType);
    }

    [TestMethod]
    public async Task The_instance_pins_to_the_version_it_started_on()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var pinned = await _host.Db.WorkflowDefinitionVersions
            .SingleAsync(v => v.Id == instance.SubWorkflowDefinitionVersionId);

        Assert.AreEqual(_subWorkflowId, pinned.WorkflowDefinitionId);
        Assert.IsTrue(pinned.IsPublished);
    }

    [TestMethod]
    public async Task A_sub_workflow_that_is_not_attached_to_the_task_is_refused()
    {
        var mainlineId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        // Attachments are what decide where a sub-workflow may hang, so this is a
        // configuration error rather than a runtime choice.
        var result = await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, mainlineId, "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "not attached");
    }

    [TestMethod]
    public async Task A_second_instance_is_refused_when_the_attachment_forbids_it()
    {
        (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var second = await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator");

        Assert.IsTrue(second.IsError);
        StringAssert.Contains(second.UnwrapError().Message, "already running");
    }

    [TestMethod]
    public async Task A_sub_workflow_cannot_be_started_as_a_run_of_its_own()
    {
        // It has no subject and no meaning apart from the parent that started it.
        var result = await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "9"), _subWorkflowId, "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "sub-workflow");
    }

    // ─────────────────────────────── Blocking ───────────────────────────────

    [TestMethod]
    public async Task A_blocking_instance_holds_the_parent_until_it_finishes()
    {
        (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var blocked = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsTrue(blocked.IsError, "the parent completed while its sub-workflow was running");
        StringAssert.Contains(blocked.UnwrapError().Message, "Technical Review");

        await CompleteSubWorkflowAsync();

        var afterwards = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsTrue(afterwards.IsOk,
            afterwards.IsError ? afterwards.UnwrapError().Message : null);
    }

    // ─────────────────────────────── Completion ───────────────────────────────

    [TestMethod]
    public async Task The_instance_closes_when_its_last_task_completes()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        // get-info, then section-review, then the terminal division-review. Only the last of
        // the three closes the instance; the two before it route onward.
        for (var step = 0; step < 2; step++)
        {
            var next = await _host.Db.WorkflowTasks
                .SingleAsync(t => t.SubWorkflowInstanceId == instance.Id
                               && t.Status == WorkflowTaskStatus.NotStarted);

            (await _host.Engine.CompleteTaskAsync(next.Id, "approved", "user-lead-c100")).Unwrap();

            var stillRunning = await _host.Db.WorkflowSubWorkflowInstances
                .AsNoTracking().SingleAsync(i => i.Id == instance.Id);

            Assert.AreEqual(SubWorkflowStatus.Running, stillRunning.Status);
        }

        var last = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.SubWorkflowInstanceId == instance.Id
                           && t.Status == WorkflowTaskStatus.NotStarted);

        (await _host.Engine.CompleteTaskAsync(last.Id, "approved", "user-division-head")).Unwrap();

        var finished = await _host.Db.WorkflowSubWorkflowInstances
            .AsNoTracking().SingleAsync(i => i.Id == instance.Id);

        Assert.AreEqual(SubWorkflowStatus.Completed, finished.Status);
        Assert.IsNotNull(finished.CompletedDate);
    }

    [TestMethod]
    public async Task Cancelling_the_parent_takes_its_sub_workflows_with_it()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        (await _host.Engine.CancelTaskAsync(_parentTaskId, "user-originator", "not needed")).Unwrap();

        var cancelled = await _host.Db.WorkflowSubWorkflowInstances
            .AsNoTracking().SingleAsync(i => i.Id == instance.Id);

        Assert.AreEqual(SubWorkflowStatus.Cancelled, cancelled.Status);

        // Left open they would be work nobody can reach, assigned to real people.
        var open = await _host.Db.WorkflowTasks
            .AsNoTracking()
            .CountAsync(t => t.SubWorkflowInstanceId == instance.Id
                          && t.Status != WorkflowTaskStatus.Cancelled
                          && t.Status != WorkflowTaskStatus.Completed);

        Assert.AreEqual(0, open);
    }

    // ─────────────────────────────── Reads ───────────────────────────────

    [TestMethod]
    public async Task The_options_say_what_can_be_started_and_what_already_is()
    {
        var before = (await _host.Engine.GetSubWorkflowOptionsAsync(_parentTaskId)).Unwrap();

        var option = before.Single();
        Assert.AreEqual("Technical Review", option.Name);
        Assert.IsTrue(option.IsBlocking);
        Assert.IsFalse(option.IsAutomatic);
        Assert.IsTrue(option.CanStart);

        (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var after = (await _host.Engine.GetSubWorkflowOptionsAsync(_parentTaskId)).Unwrap();

        // Offering it again would only produce an error the user could not have predicted.
        Assert.IsFalse(after.Single().CanStart);
    }

    [TestMethod]
    public async Task Instances_can_be_listed_for_a_run()
    {
        (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var instances = (await _host.Engine.GetSubWorkflowInstancesAsync(_runId)).Unwrap();

        var only = instances.Single();
        Assert.AreEqual("Technical Review", only.Name);
        Assert.AreEqual(1, only.Version);
        Assert.AreEqual(SubWorkflowStatus.Running, only.Status);
    }

    /// <summary>Completes every task in the running sub-workflow.</summary>
    private async Task CompleteSubWorkflowAsync()
    {
        while (true)
        {
            var next = await _host.Db.WorkflowTasks
                .Where(t => t.SubWorkflowInstanceId != null
                         && t.WorkflowRunId == _runId
                         && t.Status == WorkflowTaskStatus.NotStarted)
                .OrderBy(t => t.Id)
                .FirstOrDefaultAsync();

            if (next is null)
            {
                return;
            }

            (await _host.Engine.CompleteTaskAsync(next.Id, "approved", "user-lead-c100")).Unwrap();
        }
    }

    // ─────────────────────────────── Blocking (characterization) ───────────────────────────────

    /// <summary>
    /// A blocking instance holds its parent until it reaches a terminal state. The guard
    /// tests `Status == Running`, so Completed and Cancelled both release — which is the
    /// fix for the original system finding H1, where `Status != Completed` blocked a parent forever on
    /// a cancelled item.
    ///
    /// Only the Completed half is exercised below. Nothing can cancel an instance on its
    /// own today: `SubWorkflowStatus.Cancelled` is set solely by `CancelInstancesForAsync`,
    /// which runs when the *parent* is cancelled — and a cancelled parent can never be
    /// completed, so the release is unobservable. See the delegation spec.
    /// </summary>
    [TestMethod]
    public async Task A_blocking_instance_stops_the_parent_completing()
    {
        (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var result = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "Technical Review");
    }

    [TestMethod]
    public async Task Completing_the_instance_releases_the_parent()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        await DriveInstanceToCompletionAsync(instance.Id);

        var result = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsFalse(result.IsError, result.IsError ? result.UnwrapError().Message : null);
    }

    [TestMethod]
    public async Task A_non_blocking_instance_never_stops_the_parent()
    {
        await _host.Db.WorkflowSubWorkflowAttachments
            .Where(a => a.SubWorkflowDefinitionId == _subWorkflowId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsBlocking, false));

        _host.Db.ChangeTracker.Clear();

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        Assert.IsFalse(instance.IsBlocking);

        var result = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsFalse(result.IsError, result.IsError ? result.UnwrapError().Message : null);
    }

    /// <summary>Completes every open task in an instance until it closes itself.</summary>
    private async Task DriveInstanceToCompletionAsync(int instanceId)
    {
        for (var guard = 0; guard < 10; guard++)
        {
            var open = await _host.Db.WorkflowTasks
                .Where(t => t.SubWorkflowInstanceId == instanceId
                         && (t.Status == WorkflowTaskStatus.NotStarted
                          || t.Status == WorkflowTaskStatus.InProgress))
                .Select(t => t.Id)
                .ToListAsync();

            if (open.Count == 0)
            {
                return;
            }

            foreach (var id in open)
            {
                (await _host.Engine.CompleteTaskAsync(id, "approved", "user-lead-c100")).Unwrap();
            }

            _host.Db.ChangeTracker.Clear();
        }

        Assert.Fail($"Instance {instanceId} did not close after 10 rounds.");
    }

    // ─────────────────────────────── Standalone cancellation ───────────────────────────────

    /// <summary>the original system finding H1, now actually reachable: cancelling must release.</summary>
    [TestMethod]
    public async Task Cancelling_the_instance_releases_the_parent()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        (await _host.Engine.CancelSubWorkflowAsync(
            instance.Id, "user-originator", "not needed")).Unwrap();

        var result = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsFalse(result.IsError, result.IsError ? result.UnwrapError().Message : null);
    }

    [TestMethod]
    public async Task Cancelling_an_instance_cancels_its_open_tasks()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        (await _host.Engine.CancelSubWorkflowAsync(
            instance.Id, "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();

        var statuses = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId == instance.Id)
            .Select(t => t.Status)
            .ToListAsync();

        // Left open they would be work nobody can reach, assigned to real people.
        CollectionAssert.DoesNotContain(statuses, WorkflowTaskStatus.NotStarted);
        CollectionAssert.DoesNotContain(statuses, WorkflowTaskStatus.InProgress);
    }

    /// <summary>
    /// Forked is terminal. Cancelling an instance must leave it alone, or the record of
    /// why a task closed is overwritten by the reason a different thing closed.
    /// </summary>
    [TestMethod]
    public async Task Cancelling_an_instance_leaves_forked_tasks_alone()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var entryId = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId == instance.Id)
            .Select(t => t.Id)
            .SingleAsync();

        await _host.Db.WorkflowTasks
            .Where(t => t.Id == entryId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, WorkflowTaskStatus.Forked));

        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CancelSubWorkflowAsync(instance.Id, "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();

        var status = await _host.Db.WorkflowTasks
            .Where(t => t.Id == entryId).Select(t => t.Status).SingleAsync();

        Assert.AreEqual(WorkflowTaskStatus.Forked, status);
    }

    [TestMethod]
    public async Task An_instance_cannot_be_cancelled_twice()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        (await _host.Engine.CancelSubWorkflowAsync(instance.Id, "user-originator")).Unwrap();

        var second = await _host.Engine.CancelSubWorkflowAsync(instance.Id, "user-originator");

        Assert.IsTrue(second.IsError);
    }

    [TestMethod]
    public async Task Seeded_workers_belong_to_a_section()
    {
        var worker = await _host.Db.People
            .SingleAsync(p => p.ActorId == "user-worker-c100");

        var section = await _host.Db.Sections.SingleAsync(s => s.Id == worker.SectionId);

        Assert.AreEqual("C100", section.Code);
        Assert.AreEqual("user-lead-c100", section.SectionLeadActorId);
    }

    // ────────────────────── Attachment scope ──────────────────────

    [TestMethod]
    public async Task A_version_wide_attachment_is_offered_on_every_task()
    {
        await AttachVersionWideAsync();

        // A task the sub-workflow was never attached to by name.
        var otherTaskId = await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == _runId && t.Id != _parentTaskId)
            .Select(t => t.Id)
            .FirstAsync();

        var options = (await _host.Engine.GetSubWorkflowOptionsAsync(otherTaskId)).Unwrap();

        Assert.IsTrue(options.Any(o => o.SubWorkflowDefinitionId == _subWorkflowId));
    }

    [TestMethod]
    public async Task A_task_specific_attachment_wins_over_a_version_wide_one()
    {
        // The seeded attachment on _parentTaskId is blocking. The version-wide one is not.
        await AttachVersionWideAsync(isBlocking: false);
        await ReseedTaskSpecificAttachmentAsync();

        // The same precedence in the read, and one row per sub-workflow rather than two.
        var option = (await _host.Engine.GetSubWorkflowOptionsAsync(_parentTaskId)).Unwrap()
            .Single(o => o.SubWorkflowDefinitionId == _subWorkflowId);

        Assert.IsTrue(option.IsBlocking);

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        // The narrower row governs, so the instance is blocking.
        Assert.IsTrue(instance.IsBlocking);
    }

    /// <summary>
    /// Re-creates the seeded task-specific attachment so it is the newer of the two rows.
    ///
    /// Without this the seeded row has the lower Id and an unordered lookup returns it
    /// anyway, so the precedence test would pass whether or not precedence existed. Made
    /// the newer row, only the ordering can still pick it.
    /// </summary>
    private async Task ReseedTaskSpecificAttachmentAsync()
    {
        var taskDefinitionId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == _parentTaskId)
            .Select(t => t.TaskDefinitionId)
            .SingleAsync();

        var versionId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.Id == taskDefinitionId)
            .Select(d => d.WorkflowDefinitionVersionId)
            .SingleAsync();

        await _host.Db.WorkflowSubWorkflowAttachments
            .Where(a => a.SubWorkflowDefinitionId == _subWorkflowId
                     && a.TaskDefinitionId == taskDefinitionId)
            .ExecuteDeleteAsync();

        _host.Db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
        {
            SubWorkflowDefinitionId = _subWorkflowId,
            WorkflowDefinitionVersionId = versionId,
            TaskDefinitionId = taskDefinitionId,
            IsAutomatic = false,
            IsBlocking = true,
            AllowMultiple = false,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
    }

    /// <summary>Adds a version-wide attachment of the seeded sub-workflow.</summary>
    private async Task AttachVersionWideAsync(bool isBlocking = true)
    {
        var versionId = await _host.Db.WorkflowRuns
            .Where(r => r.Id == _runId)
            .Select(r => r.WorkflowDefinitionVersionId)
            .SingleAsync();

        _host.Db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
        {
            SubWorkflowDefinitionId = _subWorkflowId,
            WorkflowDefinitionVersionId = versionId,
            TaskDefinitionId = null,
            IsAutomatic = false,
            IsBlocking = isBlocking,
            AllowMultiple = true,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
    }

    // ──────────────────────────── Delegation ────────────────────────────

    /// <summary>
    /// Half of delegation: the named person reaches the chain intact.
    ///
    /// The seeded entry task is "Get Info" and declares no role key, so there is nothing
    /// for the resolver to look up and the delegatee survives verbatim — actor and section
    /// both. The other half, that the section then drives the reviews above it, is
    /// <see cref="The_review_resolves_to_the_delegatees_SectionLead_not_the_delegators"/>.
    /// </summary>
    [TestMethod]
    public async Task The_entry_task_goes_to_the_delegatee_when_it_declares_no_role()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator",
            new WorkflowAssignment("user-worker-c200", "C200"))).Unwrap();

        var entry = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.SubWorkflowInstanceId == instance.Id);

        Assert.AreEqual("user-worker-c200", entry.AssignedToActorId);
        Assert.AreEqual("C200", entry.AssignedBranchKey);
    }

    /// <summary>
    /// The point of the whole feature: the Section Lead review goes to the *delegatee's* Section Lead,
    /// not the delegator's. Before this, the chain inherited the parent's branch key —
    /// null on the seeded mainline — so the lookup had no section to resolve against and
    /// the review silently stayed on the delegator.
    ///
    /// The second step is checked too: the delegatee's section travels down the chain, so
    /// the branch review is C300's branch head rather than anything the parent carried.
    /// </summary>
    [TestMethod]
    public async Task The_review_resolves_to_the_delegatees_SectionLead_not_the_delegators()
    {
        var parent = await _host.Db.WorkflowTasks.AsNoTracking()
            .SingleAsync(t => t.Id == _parentTaskId);

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator",
            new WorkflowAssignment("user-worker-c300", "C300"))).Unwrap();

        var entry = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .SingleAsync(t => t.SubWorkflowInstanceId == instance.Id);

        Assert.AreEqual("Technical Review — Get Info", entry.TaskDefinition!.DisplayName);

        (await _host.Engine.CompleteTaskAsync(entry.Id, "approved", "user-worker-c300")).Unwrap();

        var sectionLead = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.SubWorkflowInstanceId == instance.Id && t.Id != entry.Id)
            .SingleAsync();

        Assert.AreEqual("Technical Review — Section Lead", sectionLead.TaskDefinition!.DisplayName);
        Assert.AreEqual("user-lead-c300", sectionLead.AssignedToActorId);
        Assert.AreEqual("C300", sectionLead.AssignedBranchKey);
        Assert.AreNotEqual(parent.AssignedToActorId, sectionLead.AssignedToActorId);

        (await _host.Engine.CompleteTaskAsync(sectionLead.Id, "approved", "user-lead-c300")).Unwrap();

        var review = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.SubWorkflowInstanceId == instance.Id
                && t.Id != entry.Id && t.Id != sectionLead.Id)
            .SingleAsync();

        Assert.AreEqual("Technical Review — Division", review.TaskDefinition!.DisplayName);
        Assert.AreEqual("C300", review.AssignedBranchKey);
        Assert.AreEqual("user-division-head", review.AssignedToActorId);
    }

    [TestMethod]
    public async Task Delegating_without_an_assignment_still_inherits_the_parent()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var entry = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.SubWorkflowInstanceId == instance.Id);

        var parent = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == _parentTaskId);

        // Unchanged behaviour when nobody is named — this is not a regression surface.
        Assert.AreEqual(parent.AssignedBranchKey, entry.AssignedBranchKey);
        Assert.AreEqual(parent.AssignedToActorId, entry.AssignedToActorId);
    }

    /// <summary>
    /// The chain begins with a role-less step so that delegation has something to land on.
    /// Every task after it carries a role key, which resolves against the section the
    /// entry task was given — so if the entry resolved too, the delegatee would never
    /// reach the chain at all.
    /// </summary>
    [TestMethod]
    public async Task The_seeded_sub_workflow_starts_with_a_get_info_step()
    {
        var version = await _host.Db.WorkflowDefinitionVersions
            .Include(v => v.Tasks).ThenInclude(t => t.TaskType)
            .SingleAsync(v => v.WorkflowDefinitionId == _subWorkflowId && v.IsPublished);

        var entry = version.Tasks.Single(t => t.Id == version.EntryTaskDefinitionId);

        Assert.AreEqual("get-info", entry.TaskType!.Key);

        // No role key: it goes to whoever the chain was delegated to, and the two
        // reviews above resolve from them.
        Assert.IsNull(entry.AssignmentRoleKey);
    }
}
