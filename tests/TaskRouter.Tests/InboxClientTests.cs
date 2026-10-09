using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using Microsoft.Extensions.Logging.Abstractions;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Runner;

namespace TaskRouter.Tests;

/// <summary>
/// The inbox: turning an opaque WorkflowSubject into something a person can read and click.
///
/// <para>This is what <see cref="IWorkflowSubjectResolver"/> exists for. The engine knows a
/// run is about "ChangeRequest:42"; only the host knows what that is called and where it
/// lives.</para>
///
/// <para><b>The inbox is the one client that must not degrade.</b> An empty inbox renders as
/// "Nothing is waiting on you", which makes swallowing a failed engine read worse than
/// throwing — the user is told the opposite of the truth and cannot tell. Subject resolution
/// around it is the other way up: a row under a raw key is visibly wrong, so it fails soft.
/// Both directions are tested below, because the pair is easy to state and easy to get
/// backwards.</para>
/// </summary>
[TestClass]
public class InboxClientTests
{
    private TestHost _host = null!;
    private EfWorkflowInboxClient _client = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();
        _client = Client();

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private EfWorkflowInboxClient Client(
        IWorkflowSubjectResolver? subjects = null,
        IWorkflowActorResolver? directory = null) =>
        new(_host.Engine,
            directory ?? new DemoDirectory(_host.Db),
            subjects ?? new DemoSubjectResolver(_host.Db),
            NullLogger<EfWorkflowInboxClient>.Instance);

    /// <summary>A subject resolver that is broken, for the fail-soft case.</summary>
    private sealed class BrokenSubjects : IWorkflowSubjectResolver
    {
        public Task<IReadOnlyDictionary<WorkflowSubject, SubjectDescriptor>> ResolveAsync(
            IReadOnlyList<WorkflowSubject> subjects, CancellationToken ct = default) =>
            throw new InvalidOperationException("The document service is down.");
    }

    /// <summary>Counts how many times it was asked, for the batching case.</summary>
    private sealed class CountingSubjects(IWorkflowSubjectResolver inner) : IWorkflowSubjectResolver
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyDictionary<WorkflowSubject, SubjectDescriptor>> ResolveAsync(
            IReadOnlyList<WorkflowSubject> subjects, CancellationToken ct = default)
        {
            Calls++;
            return inner.ResolveAsync(subjects, ct);
        }
    }

    /// <summary>
    /// A directory that answers without touching a database, and can put its actor in more
    /// than one org unit — which is the normal case in any host whose people cover several
    /// teams, and the case the seam originally could not express.
    /// </summary>
    private sealed class FixedDirectory(string actorId, params string[] units) : IWorkflowActorResolver
    {
        public Task<IReadOnlyList<string>> GetOrgUnitsForAsync(
            string id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(units);

        public Task<string?> GetDisplayNameAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<string?>("Someone");

        public Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActorOption>>([new ActorOption(actorId, "Someone")]);

        public Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BranchOption>>([]);

        public Task<WorkflowAssignment> GetAssignmentForAsync(
            string? id, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowAssignment(actorId, null));
    }

    private async Task<int> CreateDocumentAsync(string number, string title)
    {
        var changeRequest = new ChangeRequest
        {
            Title = title,
            DocNumber = number,
            Originator = "user-originator",
            CreatorId = "user-originator"
        };

        _host.Db.Documents.Add(changeRequest);
        await _host.Db.SaveChangesAsync();
        return changeRequest.Id;
    }

    private async Task<int> StartRunOnAsync(int documentId)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", documentId.ToString()),
            _definitionId,
            "user-originator")).Unwrap();

        return run.Id;
    }

    [TestMethod]
    public async Task It_resolves_the_document_label_subtitle_and_url()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        await StartRunOnAsync(documentId);

        var items = await _client.GetInboxAsync("user-originator");

        var item = items.Single();
        Assert.AreEqual("Enter Record", item.TaskLabel);
        Assert.AreEqual("CR-2026-0042", item.SubjectLabel);
        Assert.AreEqual("Pump room rewire", item.SubjectSubtitle);
        Assert.AreEqual($"documents/{documentId}", item.SubjectUrl);
    }

    [TestMethod]
    public async Task It_returns_one_row_per_task_when_a_document_has_several()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        // A second open task on the same document, standing in for a fork branch or an
        // ad-hoc addition. Built directly because what is under test is the join, not
        // how the second task came to exist.
        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);

        _host.Db.WorkflowTasks.Add(new WorkflowTask
        {
            WorkflowRunId = runId,
            TaskDefinitionId = entry.TaskDefinitionId,
            Status = WorkflowTaskStatus.NotStarted,
            AssignedToActorId = "user-originator",
            CreatorId = "user-originator",
            ModifierId = "user-originator"
        });
        await _host.Db.SaveChangesAsync();

        var items = await _client.GetInboxAsync("user-originator");

        Assert.HasCount(2, items);
        Assert.IsTrue(items.All(i => i.SubjectLabel == "CR-2026-0042"));
    }

    [TestMethod]
    public async Task It_keeps_a_row_whose_document_is_missing_and_gives_it_no_url()
    {
        // No document with id 9999. An orphaned task is a defect worth seeing, so the
        // row survives with the raw subject and nothing to click — the same reasoning
        // as not filtering the query on run status.
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "9999"), _definitionId, "user-originator")).Unwrap();

        var items = await _client.GetInboxAsync("user-originator");

        var item = items.Single(i => i.TaskId == run.Tasks.Single().Id);
        Assert.AreEqual("ChangeRequest:9999", item.SubjectLabel);
        Assert.IsNull(item.SubjectSubtitle);
        Assert.AreEqual(string.Empty, item.SubjectUrl);
    }

    [TestMethod]
    public async Task It_maps_the_workflow_name_and_branch_key_across()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        // Assigned to the actor *and* carrying a branch key. StartRunAsync leaves the
        // key null, and a null one would let a dropped mapping pass unnoticed.
        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        entry.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        var item = (await _client.GetInboxAsync("user-originator")).Single();

        Assert.AreEqual("ChangeRequest Document Review", item.WorkflowName);
        Assert.AreEqual("C200", item.BranchKey);
        Assert.IsFalse(item.IsUnclaimed, "It has an actor, so it is nobody's to claim.");
    }

    [TestMethod]
    public async Task It_carries_IsBlocked_through_the_mapping()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        // On to Provide Input, which is the task a sub-workflow attaches to.
        var entryId = await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == runId).Select(t => t.Id).SingleAsync();

        (await _host.Engine.CompleteTaskAsync(entryId, "approved", "user-originator")).Unwrap();

        var parent = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.WorkflowRunId == runId && t.Status == WorkflowTaskStatus.NotStarted);

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        (await _host.Engine.StartSubWorkflowAsync(
            parent.Id, subWorkflowId, "user-originator",
            assignment: new TaskRouter.Core.Abstractions.WorkflowAssignment(
                "user-worker-c200", "C200"))).Unwrap();

        // IsBlocked is the one field the engine builds from a second query rather than
        // from the projected row, so it is the likeliest to be lost on the way through
        // the client — hence a client-level test and not only the engine's.
        var owner = await _host.Db.WorkflowTasks
            .Where(t => t.Id == parent.Id).Select(t => t.AssignedToActorId!).SingleAsync();

        var item = (await _client.GetInboxAsync(owner)).Single(i => i.TaskId == parent.Id);

        Assert.IsTrue(item.IsBlocked, "The parent is waiting on the sub-workflow.");
        Assert.AreEqual("CR-2026-0042", item.SubjectLabel);
    }

    [TestMethod]
    public async Task It_returns_rows_oldest_first()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var firstRun = await StartRunOnAsync(documentId);
        var secondRun = await StartRunOnAsync(documentId);

        // Stamped explicitly rather than relying on two StartRunAsync calls landing on
        // different ticks — the ordering is the assertion, so it must not be a race.
        var tasks = await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == firstRun || t.WorkflowRunId == secondRun)
            .ToListAsync();

        var older = tasks.Single(t => t.WorkflowRunId == firstRun);
        var newer = tasks.Single(t => t.WorkflowRunId == secondRun);
        older.Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        newer.Created = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await _host.Db.SaveChangesAsync();

        var items = await _client.GetInboxAsync("user-originator");

        // The engine documents oldest-first and the seam promises it too; the client
        // projects rather than re-sorts, and this is what says so.
        Assert.HasCount(2, items);
        Assert.AreEqual(older.Id, items[0].TaskId);
        Assert.AreEqual(newer.Id, items[1].TaskId);
    }

    [TestMethod]
    public async Task It_asks_the_engine_for_the_actors_own_section()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        entry.AssignedToActorId = null;
        entry.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        // Drew Novak is in C200, so this unclaimed task is theirs to see. Casey Ellis is
        // in C100 and must not see it — which is the client deriving the branch keys
        // from the person rather than being told them.
        var drew = await _client.GetInboxAsync("user-worker-c200");
        var casey = await _client.GetInboxAsync("user-worker-c100");

        Assert.HasCount(1, drew);
        Assert.IsTrue(drew.Single().IsUnclaimed);
        Assert.IsEmpty(casey);
    }

    [TestMethod]
    public async Task Somebody_covering_for_another_section_can_look_at_its_queue()
    {
        // The reason the inbox can be scoped at all. Work handed to a section -- by a
        // sub-workflow, or by a fork branch -- waits there unclaimed, and until this
        // existed only that section's own people could see it had arrived.
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        entry.AssignedToActorId = null;
        entry.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        // Casey is in C100 and sees nothing of C200's, as before.
        Assert.IsEmpty(await _client.GetInboxAsync("user-worker-c100"));

        var looking = await _client.GetInboxAsync("user-worker-c100", ["C200"]);

        Assert.HasCount(1, looking);
        Assert.IsTrue(looking.Single().IsUnclaimed);
    }

    [TestMethod]
    public async Task A_unit_the_directory_does_not_list_is_ignored()
    {
        // The guard that replaces the old rule of never accepting a unit from the caller.
        // A guessed key matches nothing rather than enumerating somebody else's queue.
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        entry.AssignedToActorId = null;
        entry.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        Assert.IsEmpty(
            await _client.GetInboxAsync("user-worker-c100", ["not-a-real-unit"]),
            "An unknown key must match nothing, not everything.");
    }

    [TestMethod]
    public async Task Work_assigned_by_name_stays_visible_whatever_the_scope_is()
    {
        // It is still their inbox. Looking at another section's queue must not hide the
        // work that is actually theirs.
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        entry.AssignedToActorId = "user-worker-c100";
        await _host.Db.SaveChangesAsync();

        var items = await _client.GetInboxAsync("user-worker-c100", ["C200"]);

        Assert.HasCount(1, items);
        Assert.IsFalse(items.Single().IsUnclaimed);
    }

    [TestMethod]
    public async Task The_unit_picker_says_which_units_are_the_actors_own()
    {
        var units = await _client.GetOrgUnitsAsync("user-worker-c200");

        Assert.IsNotEmpty(units, "An empty picker offers nothing to look at.");
        Assert.IsTrue(
            units.Any(u => u.Key == "C200" && u.IsMine),
            "The picker starts on the viewer's own units, so it has to know which they are.");
        Assert.IsTrue(
            units.Any(u => !u.IsMine),
            "A picker offering only your own units is the inbox you already had.");
    }

    [TestMethod]
    public async Task The_due_date_reaches_the_inbox_item()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var due = new DateTime(2026, 11, 3, 9, 30, 0, DateTimeKind.Utc);

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        task.DueDate = due;
        await _host.Db.SaveChangesAsync();

        var items = await _client.GetInboxAsync("user-originator");

        Assert.AreEqual(due, items.Single().DueDate);
    }

    [TestMethod]
    public async Task An_item_with_no_deadline_has_a_null_due_date()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0043", "Switchboard survey");
        await StartRunOnAsync(documentId);

        // TestHost registers no due-date resolver, so the funnel stamped null.
        var items = await _client.GetInboxAsync("user-originator");

        Assert.IsNull(items.Single().DueDate);
    }

    [TestMethod]
    public async Task An_escalated_task_carries_its_escalation_stamp()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var stamped = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        entry.OverdueFiredAt = stamped;
        await _host.Db.SaveChangesAsync();

        var items = await _client.GetInboxAsync("user-originator");

        // The inbox distinguishes "late" from "late, and somebody has been told". Only
        // the second of those carries a stamp, and it has to survive the whole path —
        // InboxRow, InboxTaskSnapshot, InboxItem — to get here.
        Assert.AreEqual(stamped, items.Single().OverdueFiredAt);
    }

    [TestMethod]
    public async Task A_task_that_has_never_escalated_has_a_null_escalation_stamp()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0043", "Switchboard survey");
        await StartRunOnAsync(documentId);

        // No sweep has ever run against this task, so OverdueFiredAt was never stamped.
        var items = await _client.GetInboxAsync("user-originator");

        Assert.IsNull(items.Single().OverdueFiredAt);
    }

    [TestMethod]
    public async Task A_broken_subject_resolver_degrades_the_labels_and_not_the_inbox()
    {
        // Distinct from the missing-document case above: there the host answered and had
        // nothing to say, here the host itself failed. Both must leave the row visible,
        // because the work is still assigned and the user still has to find it.
        var documentId = await CreateDocumentAsync("CR-2026-0100", "Pump room rewire");
        await StartRunOnAsync(documentId);

        var inbox = await Client(new BrokenSubjects()).GetInboxAsync("user-originator");

        Assert.IsNotEmpty(inbox,
            "A failed document lookup emptied the inbox. The rows are the engine's; only "
            + "their labels come from the host, and only those should degrade.");
        StringAssert.StartsWith(inbox[0].SubjectLabel, "ChangeRequest:");
        Assert.AreEqual(string.Empty, inbox[0].SubjectUrl);
    }

    [TestMethod]
    public async Task A_failing_engine_read_surfaces_rather_than_reading_as_an_empty_inbox()
    {
        // The opposite policy to every other test here, deliberately. "Nothing is waiting
        // on you" is what an empty inbox says, so returning [] on a broken read tells the
        // user the exact opposite of the truth.
        //
        // A real failure rather than a mocked one: this project has no mocking library and
        // does not want one, since everything else here proves behaviour against a real
        // database. Disposing the context makes the engine's own read fail for a reason a
        // production host could genuinely hit. The directory is stubbed only so the failure
        // is definitely the engine's -- a directory over the same disposed context would
        // throw first, and the test would pass while proving nothing.
        var documentId = await CreateDocumentAsync("CR-2026-0101", "Pump room rewire");
        await StartRunOnAsync(documentId);

        var client = Client(directory: new FixedDirectory("user-originator"));

        await _host.Db.DisposeAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.GetInboxAsync("user-originator"),
            "A failed engine read must reach the caller. Swallowed, it renders as "
            + "'Nothing is waiting on you'.");
    }

    [TestMethod]
    public async Task Subjects_are_resolved_in_one_call_however_many_rows_there_are()
    {
        // The seam takes a list precisely so a forty-row inbox is not forty queries. A
        // client that looped would satisfy every other test in this class, which is why
        // this one counts rather than asserts on output.
        foreach (var number in new[] { "CR-2026-0102", "CR-2026-0103", "CR-2026-0104" })
        {
            await StartRunOnAsync(await CreateDocumentAsync(number, $"Rewire {number}"));
        }

        var counting = new CountingSubjects(new DemoSubjectResolver(_host.Db));

        var inbox = await Client(counting).GetInboxAsync("user-originator");

        Assert.IsGreaterThan(1, inbox.Count, "This proves nothing with a single row.");
        Assert.AreEqual(1, counting.Calls,
            $"{inbox.Count} rows produced {counting.Calls} calls to the subject resolver. "
            + "The seam takes a list so that number is always one.");
    }

    [TestMethod]
    public async Task An_actor_in_two_org_units_sees_unclaimed_work_in_both()
    {
        // The case a single-assignment seam could not express. The first real host to use
        // this has a many-to-many person/unit table and treats one person as covering
        // several units, so "one org unit per actor" silently hid half their inbox.
        var (first, second) = await TwoUnclaimedTasksInDifferentUnitsAsync();

        var both = await Client(directory: new FixedDirectory("user-originator", "C100", "C200"))
            .GetInboxAsync("user-originator");

        CollectionAssert.AreEquivalent(
            new[] { first, second },
            both.Select(i => i.TaskId).ToList(),
            "An actor covering two units must see the unclaimed work in both.");
    }

    [TestMethod]
    public async Task Narrowing_the_units_narrows_the_inbox()
    {
        // The other half, and the one that makes the test above mean something: if the
        // inbox ignored the list and returned everything unclaimed, the first test would
        // pass for the wrong reason.
        var (first, second) = await TwoUnclaimedTasksInDifferentUnitsAsync();

        var one = await Client(directory: new FixedDirectory("user-originator", "C100"))
            .GetInboxAsync("user-originator");

        Assert.HasCount(1, one, "Only the unit this actor covers should appear.");
        Assert.AreEqual(first, one[0].TaskId);
        Assert.AreNotEqual(second, one[0].TaskId);
    }

    [TestMethod]
    public async Task An_actor_in_no_org_unit_still_sees_work_assigned_to_them_by_name()
    {
        // No units is a real answer, not a broken one: it means "nothing unclaimed is
        // yours", and named assignments must still arrive.
        var documentId = await CreateDocumentAsync("CR-2026-0107", "Pump room rewire");
        await StartRunOnAsync(documentId);

        var inbox = await Client(directory: new FixedDirectory("user-originator"))
            .GetInboxAsync("user-originator");

        Assert.IsNotEmpty(inbox, "A named assignment does not depend on org units at all.");
    }

    /// <summary>
    /// Two runs whose entry tasks are unclaimed in different org units — assigned to a
    /// branch with nobody's name on them, which is what "waiting for someone in that
    /// section to pick up" means.
    /// </summary>
    private async Task<(int First, int Second)> TwoUnclaimedTasksInDifferentUnitsAsync()
    {
        var a = await UnclaimedTaskInAsync("CR-2026-0105", "C100");
        var b = await UnclaimedTaskInAsync("CR-2026-0106", "C200");

        return (a, b);
    }

    private async Task<int> UnclaimedTaskInAsync(string number, string unit)
    {
        var runId = await StartRunOnAsync(await CreateDocumentAsync(number, $"Rewire {number}"));
        var taskId = (await _host.Engine.GetRunAsync(runId)).Unwrap().Tasks.Single().Id;

        (await _host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment(null, unit), "user-originator", "To the section."))
            .Unwrap();

        return taskId;
    }

    [TestMethod]
    public async Task A_section_is_named_rather_than_shown_as_its_key()
    {
        // Branch keys are opaque and frequently ids -- the first host's are section row ids --
        // so an inbox rendering the key raw put a bare "3" in the Section column in front of
        // people. The directory already knows what they are called.
        var documentId = await CreateDocumentAsync("CR-2026-0110", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var taskId = (await _host.Engine.GetRunAsync(runId)).Unwrap().Tasks.Single().Id;
        var unit = (await new DemoDirectory(_host.Db).GetBranchOptionsAsync())[0];

        (await _host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment("user-originator", unit.Key), "user-originator"))
            .Unwrap();

        var row = (await Client().GetInboxAsync("user-originator")).Single(i => i.TaskId == taskId);

        Assert.AreEqual(unit.Key, row.BranchKey, "The key itself is still carried.");
        Assert.AreEqual(unit.DisplayName, row.BranchLabel);
        Assert.AreNotEqual(row.BranchKey, row.BranchLabel,
            "If these were the same the test could not tell a resolved name from the key.");
    }

    [TestMethod]
    public async Task An_unresolvable_section_keeps_its_key_rather_than_going_blank()
    {
        // A branch key the directory does not know -- a section archived since the task was
        // assigned, say. The row must still say which one it was.
        var documentId = await CreateDocumentAsync("CR-2026-0111", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var taskId = (await _host.Engine.GetRunAsync(runId)).Unwrap().Tasks.Single().Id;

        (await _host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment("user-originator", "no-such-unit"), "user-originator"))
            .Unwrap();

        var row = (await Client().GetInboxAsync("user-originator")).Single(i => i.TaskId == taskId);

        Assert.AreEqual("no-such-unit", row.BranchKey);
        Assert.IsNull(row.BranchLabel, "Unresolved means the component falls back to the key.");
    }
}
