using TaskRouter.Blazor;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;
using TaskRouter.Core.Validation;

namespace TaskRouter.Tests;

/// <summary>
/// A task is finished by choosing one of its outcomes, so a task that declares none cannot
/// be finished from a screen — and when it is the last step, neither can the run.
///
/// <para>This used to validate, publish and run, and stop at the final task.</para>
/// </summary>
[TestClass]
public class CompletableTaskTests
{
    private static WorkflowTaskDefinition Task(
        int id, string name, bool terminal = false, bool adHoc = false, params string[] outcomes)
    {
        var task = new WorkflowTaskDefinition
        {
            Id = id,
            WorkflowDefinitionVersionId = 1,
            TaskTypeDefinitionId = 1,
            DisplayName = name,
            IsTerminal = terminal,
            IsAdHoc = adHoc,
        };

        foreach (var key in outcomes)
        {
            task.ValidOutcomes.Add(new TaskOutcomeDefinition
            {
                Id = (id * 100) + task.ValidOutcomes.Count,
                TaskDefinitionId = id,
                OutcomeKey = key,
                DisplayName = key,
            });
        }

        return task;
    }

    /// <summary>Review routes to Close on "approved". Everything else is the test's.</summary>
    private static WorkflowDefinitionVersion Workflow(
        WorkflowTaskDefinition close, params WorkflowTaskDefinition[] others)
    {
        var review = Task(1, "Review", outcomes: "approved");

        review.OutgoingRoutes.Add(new TaskRoute
        {
            TaskDefinitionId = review.Id,
            TaskOutcomeDefinitionId = review.ValidOutcomes.First().Id,
            Outcome = review.ValidOutcomes.First(),
            NextTaskDefinitionId = close.Id,
            IsDefault = true,
        });

        var version = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = 1,
            Version = 1,
            EntryTaskDefinitionId = review.Id,
        };

        foreach (var task in new[] { review, close }.Concat(others))
        {
            version.Tasks.Add(task);
        }

        return version;
    }

    private static List<string> Codes(WorkflowDefinitionVersion version) =>
        [.. WorkflowDefinitionValidator.Validate(version).Select(e => e.Code)];

    [TestMethod]
    public void A_terminal_task_with_an_outcome_is_valid() =>
        Assert.IsEmpty(Codes(Workflow(Task(2, "Close", terminal: true, outcomes: "closed"))));

    [TestMethod]
    public void A_terminal_task_with_no_outcome_is_refused_and_named()
    {
        var errors = WorkflowDefinitionValidator.Validate(Workflow(Task(2, "Close", terminal: true)));

        Assert.HasCount(1, errors);
        Assert.AreEqual("NO_OUTCOME", errors[0].Code);
        Assert.AreEqual(2, errors[0].TaskDefinitionId);
        StringAssert.Contains(errors[0].Message, "Close");
    }

    [TestMethod]
    public void An_outcome_that_has_been_archived_does_not_count()
    {
        var close = Task(2, "Close", terminal: true, outcomes: "closed");
        close.ValidOutcomes.First().IsArchived = true;

        CollectionAssert.Contains(Codes(Workflow(close)), "NO_OUTCOME");
    }

    [TestMethod]
    public void An_ad_hoc_task_with_no_outcome_is_refused_too()
    {
        // The same dead stop, and worse when it blocks its parent: the parent cannot be
        // completed until this is, and this never can be.
        var version = Workflow(
            Task(2, "Close", terminal: true, outcomes: "closed"),
            Task(3, "Extra check", adHoc: true));

        CollectionAssert.AreEqual(new[] { "NO_OUTCOME" }, Codes(version));
    }

    [TestMethod]
    public void An_ad_hoc_task_with_an_outcome_and_no_routes_is_still_a_legitimate_leaf()
    {
        var version = Workflow(
            Task(2, "Close", terminal: true, outcomes: "closed"),
            Task(3, "Extra check", adHoc: true, outcomes: "done"));

        Assert.IsEmpty(Codes(version));
    }

    [TestMethod]
    public void The_runner_offers_a_tasks_own_outcomes_when_it_has_any()
    {
        OutcomeOption[] declared = [new("approved", "Approved", 1), new("rejected", "Rejected", 2)];

        Assert.AreSame(declared, DefaultOutcome.IfNone(declared));
    }

    [TestMethod]
    public void The_runner_offers_one_way_to_finish_a_task_that_declares_none()
    {
        // Workflows published before the rule above are still running. The engine accepts
        // any key for such a task, so this is a button it honours.
        var offered = DefaultOutcome.IfNone([]);

        Assert.HasCount(1, offered);
        Assert.AreEqual("completed", offered[0].OutcomeKey);
        Assert.AreEqual("Complete", offered[0].DisplayName);
    }

    [TestMethod]
    public void The_builder_adds_the_default_when_the_host_publishes_no_list()
    {
        var added = DefaultOutcome.For([]);

        Assert.AreEqual("completed", added?.OutcomeKey);
        Assert.AreEqual("Complete", added?.DisplayName);
    }

    [TestMethod]
    public void The_builder_uses_the_hosts_own_name_for_it_when_the_list_has_it()
    {
        var added = DefaultOutcome.For(
            [new OutcomeTypeOption(1, "approved", "Approved"), new OutcomeTypeOption(2, "Completed", "All done")]);

        Assert.AreEqual("Completed", added?.OutcomeKey);
        Assert.AreEqual("All done", added?.DisplayName);
    }

    [TestMethod]
    public void The_builder_does_not_guess_outside_a_list_the_host_published() =>
        // A key the host cannot read would be worse than asking the author to pick one.
        Assert.IsNull(DefaultOutcome.For([new OutcomeTypeOption(1, "approved", "Approved")]));
}
