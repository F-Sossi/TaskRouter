using System.Text.Json;

using TaskRouter.Core.Inbox;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;

namespace TaskRouter.Tests;

/// <summary>
/// The runner's and inbox's view records were written to be passed between objects in one
/// process. Sending them over HTTP is a new requirement on them, and the failure mode is
/// quiet: a property JSON cannot populate arrives on the far side as a default value, not as
/// an error. A task would silently lose its due date, or arrive claiming
/// <c>CanComplete</c> when it cannot.
///
/// <para>These run <b>before</b> the endpoints exist, which is the point. The builder's
/// equivalent caught a property that serialized under two names and corrupted itself on the
/// way back; six tasks were then built on a wire format already known to be sound.</para>
///
/// <para>Every value below is deliberately <b>not</b> the property's default — no zeroes, no
/// nulls except where null is the thing being tested, no <c>default(DateTime)</c> — so a
/// field that silently fails to round-trip fails a test rather than passing by
/// coincidence.</para>
/// </summary>
[TestClass]
public class RunnerSerializationTests
{
    /// <summary>The options the endpoints and the HTTP clients will use.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static readonly Guid ForkGroup = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    // ─────────────────────────────── Fixtures ───────────────────────────────

    private static RunTaskView FullyPopulatedTask() => new(
        Id: 11,
        TaskDefinitionId: 12,
        TaskTypeKey: "branch-review",
        Label: "Branch Review",
        Status: WorkflowTaskStatus.Forked,          // 4, not the 0 default
        OutcomeKey: "approved",
        AssignedToActorId: "actor-7",
        AssignedToDisplayName: "Dana Reyes",
        AssignedBranchKey: "C200",
        DueDate: new DateTime(2026, 9, 30, 17, 45, 3, DateTimeKind.Utc),
        Notes: "Needs a second look at the pump schedule.",
        ParentTaskId: 10,
        IsAdHoc: true,
        IsBlocking: true,
        IsForkOrigin: true,
        IsForkable: true,
        RunningSubWorkflowCount: 3,
        BlockingSubWorkflowCount: 2,
        HasSubWorkflowOptions: true,
        IsConvergencePoint: true,
        ForkGroupId: ForkGroup,
        Created: new DateTime(2026, 9, 1, 8, 15, 30, DateTimeKind.Utc),
        CompletedDate: new DateTime(2026, 9, 2, 9, 16, 31, DateTimeKind.Utc),
        CanComplete: true,
        OverdueFiredAt: new DateTime(2026, 9, 3, 10, 17, 32, DateTimeKind.Utc));

    private static RunView FullyPopulatedRun() => new(
        Id: 21,
        SubjectType: "ChangeRequest",
        SubjectId: "42",
        WorkflowName: "Document Review",
        Version: 3,
        Status: WorkflowRunStatus.Cancelled,        // 2, not the 0 default
        IsTest: true,
        Created: new DateTime(2026, 8, 30, 7, 5, 1, DateTimeKind.Utc),
        OpenTaskCount: 4,
        PercentComplete: 62.5);

    private static InboxItem FullyPopulatedInboxItem() => new(
        TaskId: 31,
        TaskLabel: "Provide Input",
        WorkflowName: "Document Review",
        SubjectLabel: "CR-2026-0042",
        SubjectSubtitle: "Pump room rewire",
        SubjectUrl: "documents/42",
        BranchKey: "C100",
        IsUnclaimed: true,
        IsBlocked: true,
        Created: new DateTime(2026, 9, 1, 6, 4, 2, DateTimeKind.Utc),
        DueDate: new DateTime(2026, 9, 15, 23, 59, 58, DateTimeKind.Utc),
        OverdueFiredAt: new DateTime(2026, 9, 16, 1, 2, 3, DateTimeKind.Utc));

    // ─────────────────────────────── Tests ───────────────────────────────

    [TestMethod]
    public void Every_one_of_RunTaskViews_twenty_five_fields_survives()
    {
        // Record equality compares all 25, so this fails naming the whole value rather than
        // one assertion at a time -- and it cannot be defeated by a field nobody remembered
        // to assert on, which is the failure mode a hand-written list of 25 asserts has.
        var original = FullyPopulatedTask();

        Assert.AreEqual(original, RoundTrip(original));
    }

    [TestMethod]
    public void RunTaskViews_dates_stay_UTC_and_keep_their_seconds()
    {
        // Equality above would catch a lost date, but not tell you which one, and a
        // DateTime that round-trips through a local-time representation comes back equal on
        // a machine at UTC+0 and wrong everywhere else. Assert the Kind explicitly.
        var back = RoundTrip(FullyPopulatedTask());

        foreach (var (name, value) in new (string, DateTime?)[]
        {
            (nameof(back.DueDate), back.DueDate),
            (nameof(back.Created), back.Created),
            (nameof(back.CompletedDate), back.CompletedDate),
            (nameof(back.OverdueFiredAt), back.OverdueFiredAt),
        })
        {
            Assert.IsNotNull(value, $"{name} did not survive at all.");
            Assert.AreEqual(DateTimeKind.Utc, value.Value.Kind,
                $"{name} came back as {value.Value.Kind}. Every date in these views is "
                + "documented as UTC, and a renderer comparing it against DateTime.Now "
                + "would be silently wrong by the host's offset.");
        }
    }

    [TestMethod]
    public void A_run_and_its_nested_tasks_survive_together()
    {
        // RunDetail nests a list of RunTaskView inside a RunView. Nesting is the first thing
        // a shallow serialization loses, and it loses it as an empty list rather than an
        // error -- a run that renders with no tasks at all.
        var original = new RunDetail(FullyPopulatedRun(), [FullyPopulatedTask(), FullyPopulatedTask()]);
        var back = RoundTrip(original);

        Assert.AreEqual(original.Run, back.Run);
        Assert.HasCount(2, back.Tasks, "The nested task list did not survive.");
        Assert.AreEqual(original.Tasks[0], back.Tasks[0]);
    }

    [TestMethod]
    public void Fork_state_survives_including_its_branches()
    {
        var original = new ForkInfo(
            IsPartOfFork: true,
            ForkGroupId: ForkGroup,
            IsConvergenceTask: true,
            ForkManifestId: 51,
            CompletedBranchCount: 2,
            CancelledBranchCount: 1,
            PendingBranchCount: 3,
            Branches:
            [
                new ForkBranchView(
                    BranchKey: "C100",
                    TaskId: 61,
                    Status: WorkflowTaskStatus.Cancelled,
                    OutcomeKey: "rejected",
                    AssignedToActorId: "actor-9",
                    AssignedToDisplayName: "Sam Okafor",
                    Notes: "Sent back for rework."),
            ]);

        var back = RoundTrip(original);

        // Compared in two halves on purpose. A positional record's generated Equals uses
        // *reference* equality for a collection member, so AreEqual on the whole ForkInfo
        // fails even when every value is identical -- swapping in one shared empty list
        // makes the scalar comparison mean what it looks like it means. Do not "fix" this
        // by loosening it back to a single AreEqual; it will fail again for a reason that
        // has nothing to do with serialization.
        IReadOnlyList<ForkBranchView> none = [];

        Assert.AreEqual(original with { Branches = none }, back with { Branches = none },
            "One of the fork's seven scalar fields did not survive.");

        Assert.HasCount(1, back.Branches);
        Assert.AreEqual(original.Branches[0], back.Branches[0],
            "Selective rejection is chosen from these branches, so a branch that loses its "
            + "key or its outcome makes the convergence dialog offer the wrong choices.");
    }

    [TestMethod]
    public void An_inbox_item_survives_whole()
    {
        var original = FullyPopulatedInboxItem();

        Assert.AreEqual(original, RoundTrip(original));
    }

    [TestMethod]
    public void An_unresolved_inbox_subject_stays_unresolved_rather_than_becoming_empty()
    {
        // The documented fallback: a subject the host cannot describe keeps its row, under
        // the raw key and with nothing to click. If the null subtitle came back as "" the
        // row would render a blank second line, and if the raw label were lost the row
        // would be unidentifiable -- both of which hide a defect the fallback exists to show.
        var original = FullyPopulatedInboxItem() with
        {
            SubjectLabel = "ChangeRequest:9999",
            SubjectSubtitle = null,
            SubjectUrl = "",
        };

        var back = RoundTrip(original);

        Assert.AreEqual("ChangeRequest:9999", back.SubjectLabel);
        Assert.IsNull(back.SubjectSubtitle, "A null subtitle must not become an empty string.");
        Assert.AreEqual("", back.SubjectUrl);
    }

    [TestMethod]
    public void A_task_with_nothing_set_keeps_its_nulls()
    {
        // The mirror of the fully-populated case. Every nullable defaulting to null on the
        // far side would make the populated test pass for the wrong reason if the
        // serializer were dropping fields wholesale, so prove nulls are carried as nulls.
        var original = new RunTaskView(
            Id: 1, TaskDefinitionId: 2, TaskTypeKey: "t", Label: "L",
            Status: WorkflowTaskStatus.NotStarted, OutcomeKey: null,
            AssignedToActorId: null, AssignedToDisplayName: null, AssignedBranchKey: null,
            DueDate: null, Notes: null, ParentTaskId: null,
            IsAdHoc: false, IsBlocking: false, IsForkOrigin: false, IsForkable: false,
            RunningSubWorkflowCount: 0, BlockingSubWorkflowCount: 0,
            HasSubWorkflowOptions: false, IsConvergencePoint: false, ForkGroupId: null,
            Created: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            CompletedDate: null, CanComplete: false, OverdueFiredAt: null);

        Assert.AreEqual(original, RoundTrip(original));
    }

    [TestMethod]
    public void The_smaller_views_survive()
    {
        // Each is a dropdown or a list somewhere in the runner. None is interesting alone;
        // all of them are a route each, and a silent default on any one of them is a
        // control that renders wrong rather than an error anybody sees.
        Assert.AreEqual(
            new OutcomeOption("rejected", "Rejected", 4, IsRework: true),
            RoundTrip(new OutcomeOption("rejected", "Rejected", 4, IsRework: true)));

        Assert.AreEqual(
            new ConvergenceOption(71, "Final Review", true),
            RoundTrip(new ConvergenceOption(71, "Final Review", true)));

        Assert.AreEqual(new AdHocOption(81, "Extra Check"), RoundTrip(new AdHocOption(81, "Extra Check")));

        Assert.AreEqual(
            new SubWorkflowOptionView(91, "Legal Review", IsBlocking: true, CanStart: true),
            RoundTrip(new SubWorkflowOptionView(91, "Legal Review", IsBlocking: true, CanStart: true)));

        Assert.AreEqual(
            new SubWorkflowInstanceView(101, 102, "Legal Review", IsBlocking: true, IsRunning: true),
            RoundTrip(new SubWorkflowInstanceView(101, 102, "Legal Review", IsBlocking: true, IsRunning: true)));

        var log = new LogEntryView(
            111, "Completed", "approved", "actor-3", "Lee Park",
            new DateTime(2026, 9, 4, 11, 12, 13, DateTimeKind.Utc));

        Assert.AreEqual(log, RoundTrip(log));
    }

    [TestMethod]
    public void The_result_types_survive_both_of_their_states()
    {
        // These carry whether an operation worked. A RunnerResult that deserializes to
        // Success = false on the happy path turns every successful action into an error
        // toast; one that deserializes to true on the sad path hides a real failure.
        Assert.AreEqual(RunnerResult.Ok, RoundTrip(RunnerResult.Ok));
        Assert.AreEqual(RunnerResult.Failed("no"), RoundTrip(RunnerResult.Failed("no")));
        Assert.AreEqual(TestRunResult.Started(7), RoundTrip(TestRunResult.Started(7)));
        Assert.AreEqual(TestRunResult.Failed("no"), RoundTrip(TestRunResult.Failed("no")));
    }

    [TestMethod]
    public void The_wire_actually_carries_all_twenty_five_field_names()
    {
        // Proof the round-trip tests are not vacuous. If a future [JsonIgnore] dropped a
        // field from the wire, both sides of a round trip would agree on its default and
        // AreEqual would still pass -- while the real client, talking to a real server,
        // would silently lose it. This is the assertion that cannot be fooled that way.
        var json = JsonSerializer.Serialize(FullyPopulatedTask(), Options);

        foreach (var name in new[]
        {
            "id", "taskDefinitionId", "taskTypeKey", "label", "status", "outcomeKey",
            "assignedToActorId", "assignedToDisplayName", "assignedBranchKey", "dueDate",
            "notes", "parentTaskId", "isAdHoc", "isBlocking", "isForkOrigin", "isForkable",
            "runningSubWorkflowCount", "blockingSubWorkflowCount", "hasSubWorkflowOptions",
            "isConvergencePoint", "forkGroupId", "created", "completedDate", "canComplete",
            "overdueFiredAt",
        })
        {
            StringAssert.Contains(json, $"\"{name}\":",
                $"RunTaskView.{name} is not on the wire, so it cannot reach a client.");
        }
    }
}
