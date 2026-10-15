using TaskRouter.Blazor;
using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

[TestClass]
public class TriggerSelectionTests
{
    private static readonly TriggerDescriptor UpdateFields = new(
        "workflow.updateFields", "Update fields", "Sets fields on the subject.",
        [WorkflowEventKind.TaskCompleted, WorkflowEventKind.TaskCreated],
        [
            new("target", "Target", TriggerParameterKind.FieldTarget, Required: true),
            new("assignments", "Fields", TriggerParameterKind.FieldAssignments, Required: true)
        ],
        DefaultDispatch: TriggerDispatchMode.InTransaction,
        DefaultFailurePolicy: TriggerFailurePolicy.FailOperation);

    private static readonly TriggerDescriptor Webhook = new(
        "workflow.webhook", "Webhook", "Posts somewhere.",
        [WorkflowEventKind.TaskCompleted],
        [new("url", "URL", TriggerParameterKind.Url, DefaultValue: "https://example.test")],
        DefaultDispatch: TriggerDispatchMode.AfterCommit);

    [TestMethod]
    public void A_new_trigger_takes_the_actions_dispatch_and_failure_defaults()
    {
        var edit = new TriggerEditModel();

        TriggerSelection.Apply(edit, UpdateFields, isNew: true);

        Assert.AreEqual("workflow.updateFields", edit.TriggerKey);
        Assert.AreEqual(TriggerDispatchMode.InTransaction, edit.DispatchMode);
        Assert.AreEqual(TriggerFailurePolicy.FailOperation, edit.FailurePolicy);
    }

    [TestMethod]
    public void A_new_trigger_follows_the_action_it_was_switched_to()
    {
        var edit = new TriggerEditModel();

        TriggerSelection.Apply(edit, Webhook, isNew: true);
        TriggerSelection.Apply(edit, UpdateFields, isNew: true);

        Assert.AreEqual(TriggerDispatchMode.InTransaction, edit.DispatchMode);
        Assert.AreEqual(TriggerFailurePolicy.FailOperation, edit.FailurePolicy);
    }

    [TestMethod]
    public void An_existing_trigger_keeps_its_saved_dispatch_and_failure_choice()
    {
        var edit = new TriggerEditModel
        {
            TriggerKey = "workflow.webhook",
            DispatchMode = TriggerDispatchMode.Background,
            FailurePolicy = TriggerFailurePolicy.LogAndContinue
        };

        TriggerSelection.Apply(edit, UpdateFields, isNew: false);

        Assert.AreEqual("workflow.updateFields", edit.TriggerKey);
        Assert.AreEqual(TriggerDispatchMode.Background, edit.DispatchMode);
        Assert.AreEqual(TriggerFailurePolicy.LogAndContinue, edit.FailurePolicy);
    }

    [TestMethod]
    public void It_drops_configuration_of_the_previous_action_and_fills_defaults()
    {
        var edit = new TriggerEditModel
        {
            Configuration = new() { ["target"] = "document", ["assignments"] = "[]" }
        };

        TriggerSelection.Apply(edit, Webhook, isNew: true);

        Assert.HasCount(1, edit.Configuration);
        Assert.AreEqual("https://example.test", edit.Configuration["url"]);
    }

    [TestMethod]
    public void It_keeps_configuration_already_set_over_a_parameter_default()
    {
        var edit = new TriggerEditModel { Configuration = new() { ["url"] = "https://mine.test" } };

        TriggerSelection.Apply(edit, Webhook, isNew: true);

        Assert.AreEqual("https://mine.test", edit.Configuration["url"]);
    }

    [TestMethod]
    public void It_moves_the_event_onto_one_the_action_handles()
    {
        var edit = new TriggerEditModel { Event = WorkflowEventKind.TaskCreated };

        TriggerSelection.Apply(edit, Webhook, isNew: true);

        Assert.AreEqual(WorkflowEventKind.TaskCompleted, edit.Event);
    }
}
