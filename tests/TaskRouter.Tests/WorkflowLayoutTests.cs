using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Builder;
using TaskRouter.EntityFrameworkCore.Builder;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// Ordering and diagram generation are pure functions over the edit model, so these
/// need no database except for the one test that checks the seeded workflow comes out
/// in an order a reader would recognise.
/// </summary>
[TestClass]
public class WorkflowLayoutTests
{
    private static TaskEditModel Task(string key, bool terminal = false, bool adHoc = false) =>
        new() { TaskTypeKey = key, DisplayName = key, IsTerminal = terminal, IsAdHoc = adHoc };

    private static void Route(TaskEditModel from, string outcome, TaskEditModel to, bool rework = false) =>
        from.Routes.Add(new RouteEditModel
        {
            OutcomeKey = outcome,
            NextTaskLocalId = to.LocalId,
            IsReworkRoute = rework
        });

    [TestMethod]
    public void Tasks_come_back_in_the_order_a_run_would_meet_them()
    {
        var first = Task("first");
        var second = Task("second");
        var third = Task("third", terminal: true);

        Route(first, "approved", second);
        Route(second, "approved", third);

        // Deliberately added back to front: insertion order is what this has to ignore.
        var model = new WorkflowEditModel { Name = "Chain", Tasks = { third, second, first } };
        model.EntryTaskLocalId = first.LocalId;

        var ordered = WorkflowLayout.Order(model).Select(t => t.TaskTypeKey).ToList();

        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, ordered);
    }

    [TestMethod]
    public void A_rework_route_does_not_drag_an_early_task_to_the_end()
    {
        var enter = Task("enter");
        var input = Task("input");
        var review = Task("review");
        var close = Task("close", terminal: true);

        Route(enter, "approved", input);
        Route(input, "approved", review);
        Route(review, "approved", close);
        Route(review, "rejected", input, rework: true);   // the cycle

        var model = new WorkflowEditModel { Name = "Rework", Tasks = { enter, input, review, close } };
        model.EntryTaskLocalId = enter.LocalId;

        var ordered = WorkflowLayout.Order(model).Select(t => t.TaskTypeKey).ToList();

        CollectionAssert.AreEqual(new[] { "enter", "input", "review", "close" }, ordered);
    }

    [TestMethod]
    public void Ad_hoc_chains_follow_the_mainline_and_stay_in_chain_order()
    {
        var enter = Task("enter");
        var close = Task("close", terminal: true);
        Route(enter, "approved", close);

        var adHocRoot = Task("adhoc-input", adHoc: true);
        var adHocNext = Task("adhoc-review", adHoc: true);
        Route(adHocRoot, "approved", adHocNext);

        var model = new WorkflowEditModel
        {
            Name = "With ad-hoc",
            Tasks = { adHocNext, enter, adHocRoot, close }
        };
        model.EntryTaskLocalId = enter.LocalId;

        var ordered = WorkflowLayout.Order(model).Select(t => t.TaskTypeKey).ToList();

        CollectionAssert.AreEqual(
            new[] { "enter", "close", "adhoc-input", "adhoc-review" }, ordered);
    }

    [TestMethod]
    public void An_unreachable_task_is_kept_rather_than_dropped()
    {
        var enter = Task("enter", terminal: true);
        var stranded = Task("stranded");

        var model = new WorkflowEditModel { Name = "Stranded", Tasks = { enter, stranded } };
        model.EntryTaskLocalId = enter.LocalId;

        var ordered = WorkflowLayout.Order(model).ToList();

        Assert.HasCount(2, ordered, "a task nothing routes to must stay visible to be fixed");
        Assert.AreEqual("stranded", ordered[^1].TaskTypeKey);
    }

    [TestMethod]
    public void Ordering_never_loses_or_duplicates_a_task()
    {
        var a = Task("a");
        var b = Task("b");
        var c = Task("c");

        // Two routes converge on c, so a naive walk would emit it twice.
        Route(a, "approved", b);
        Route(a, "rejected", c);
        Route(b, "approved", c);

        var model = new WorkflowEditModel { Name = "Diamond", Tasks = { a, b, c } };
        model.EntryTaskLocalId = a.LocalId;

        var ordered = WorkflowLayout.Order(model).ToList();

        Assert.HasCount(3, ordered);
        Assert.AreEqual(3, ordered.Select(t => t.LocalId).Distinct().Count());
    }

    [TestMethod]
    public void ApplyOrder_rewrites_the_model_without_changing_its_meaning()
    {
        var first = Task("first");
        var second = Task("second", terminal: true);
        Route(first, "approved", second);

        var model = new WorkflowEditModel { Name = "Chain", Tasks = { second, first } };
        model.EntryTaskLocalId = first.LocalId;

        WorkflowLayout.ApplyOrder(model);

        Assert.AreEqual("first", model.Tasks[0].TaskTypeKey);

        // Routes are held by local id, so the route must still resolve after reordering.
        Assert.AreEqual(second.LocalId, model.Tasks[0].Routes[0].NextTaskLocalId);
        Assert.IsNotNull(model.Find(model.Tasks[0].Routes[0].NextTaskLocalId!.Value));
    }

    // ─────────────────────────────── Mermaid ───────────────────────────────

    [TestMethod]
    public void The_flowchart_encodes_each_task_role_in_its_shape()
    {
        var entry = Task("entry");
        var fork = Task("fork");
        var converge = Task("converge");
        var close = Task("close", terminal: true);

        fork.IsForkable = true;
        converge.IsConvergencePoint = true;

        Route(entry, "approved", fork);
        Route(fork, "approved", converge);
        Route(converge, "approved", close);
        Route(converge, "rejected", fork, rework: true);

        var model = new WorkflowEditModel { Name = "Shapes", Tasks = { entry, fork, converge, close } };
        model.EntryTaskLocalId = entry.LocalId;

        var chart = WorkflowMermaid.ToFlowchart(model);

        StringAssert.StartsWith(chart, "flowchart TD");
        StringAssert.Contains(chart, "([\"entry\"])");      // stadium: entry
        StringAssert.Contains(chart, "{{\"fork\"}}");       // hexagon: forkable
        StringAssert.Contains(chart, "[/\"converge\"/]");   // trapezoid: convergence
        StringAssert.Contains(chart, "[[\"close\"]]");      // double square: terminal
        StringAssert.Contains(chart, ".-> ");               // dotted: the rework route
    }

    [TestMethod]
    public void Each_task_role_is_coloured_as_well_as_shaped()
    {
        // Shape alone asks a reader to know that a hexagon forks and a parallelogram
        // converges. Colour tells them which nodes are alike before they have learned the
        // vocabulary, so every node carries a class and every class has a fill.
        var entry = Task("entry");
        var fork = Task("fork");
        var converge = Task("converge");
        var close = Task("close", terminal: true);
        var optional = Task("optional");

        fork.IsForkable = true;
        converge.IsConvergencePoint = true;
        optional.IsAdHoc = true;

        var model = new WorkflowEditModel
        {
            Name = "Coloured",
            EntryTaskLocalId = entry.LocalId,
            Tasks = [entry, fork, converge, close, optional]
        };

        var chart = WorkflowMermaid.ToFlowchart(model);

        foreach (var kind in new[] { "entry", "forkable", "convergence", "terminal", "adhoc" })
        {
            StringAssert.Contains(chart, $":::{kind}", $"no node was classed {kind}");
            StringAssert.Contains(chart, $"classDef {kind} fill:", $"{kind} has no fill");
        }

        // Ad-hoc wins where a task is both: "this step is optional" changes how to read the
        // whole path through it. Counted rather than matched by node id, which is positional
        // over a computed order and none of this test's business.
        optional.IsForkable = true;
        var both = WorkflowMermaid.ToFlowchart(model);

        Assert.AreEqual(1, Occurrences(both, ":::adhoc\n"),
            "The task that is both should be classed once, as ad-hoc.");
        Assert.AreEqual(1, Occurrences(both, ":::forkable\n"),
            "Only the genuinely forkable task is, so ad-hoc did not lose the tie.");
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [TestMethod]
    public void A_label_that_would_break_the_parser_is_escaped()
    {
        var task = Task("odd", terminal: true);
        task.DisplayName = "Review \"urgent\" <items> & sign";

        var model = new WorkflowEditModel { Name = "Escaping", Tasks = { task } };
        model.EntryTaskLocalId = task.LocalId;

        var chart = WorkflowMermaid.ToFlowchart(model);

        StringAssert.Contains(chart, "#quot;urgent#quot;");
        StringAssert.Contains(chart, "#lt;items#gt;");
        StringAssert.Contains(chart, "#amp;");

        // One opening and one closing quote per label, or Mermaid stops parsing there.
        Assert.AreEqual(0, chart.Count(c => c == '"') % 2);
    }

    [TestMethod]
    public void An_empty_workflow_still_produces_a_parseable_chart()
    {
        var chart = WorkflowMermaid.ToFlowchart(new WorkflowEditModel { Name = "Nothing" });

        StringAssert.StartsWith(chart, "flowchart TD");
        StringAssert.Contains(chart, "No tasks yet");
    }

    [TestMethod]
    public void A_route_pointing_nowhere_is_skipped_rather_than_emitting_a_broken_edge()
    {
        var task = Task("only", terminal: true);
        task.Routes.Add(new RouteEditModel { OutcomeKey = "approved", NextTaskLocalId = Guid.NewGuid() });

        var model = new WorkflowEditModel { Name = "Dangling", Tasks = { task } };
        model.EntryTaskLocalId = task.LocalId;

        var chart = WorkflowMermaid.ToFlowchart(model);

        Assert.DoesNotContain("-->", chart);
    }

    // ────────────────────────── Against the real workflow ──────────────────────────

    [TestMethod]
    public async Task The_seeded_workflow_reads_in_execution_order()
    {
        await using var host = await TestHost.CreateAsync();

        var client = new EfWorkflowBuilderClient(
            host.Db,
            new WorkflowTriggerRegistry([new SetVariableTrigger(), new ReportProgressTrigger()]),
            [new RequiresReviewCondition()],
            new WorkflowBuilderOptions(),
            new TestEditorActorAccessor());

        var versionId = await host.Db.WorkflowDefinitionVersions
            .Where(v => v.IsPublished && v.IsLatest && !v.WorkflowDefinition!.IsSubWorkflow)
            .Select(v => v.Id)
            .SingleAsync();

        var model = (await client.GetWorkflowAsync(versionId))!;
        var ordered = WorkflowLayout.Order(model).Select(t => t.Label).ToList();

        // Mainline first, in the order a ChangeRequest actually travels.
        CollectionAssert.AreEqual(
            new[] { "Enter Record", "Provide Input", "PM Review", "Close Document" },
            ordered.Take(4).ToList());

        // Then the ad-hoc chain, in chain order.
        CollectionAssert.AreEqual(
            new[] { "Ad-hoc Provide Input", "Ad-hoc Section Review", "Ad-hoc Division Review" },
            ordered.Skip(4).ToList());

        var chart = WorkflowMermaid.ToFlowchart(model);
        StringAssert.Contains(chart, "Enter Record");
        StringAssert.Contains(chart, ".-> ");   // PM Review's rework route back to Provide Input
    }
}
