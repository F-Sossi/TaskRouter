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
    public void A_trigger_takes_the_actions_dispatch_and_failure_defaults()
    {
        var edit = new TriggerEditModel();

        TriggerSelection.Apply(edit, UpdateFields);

        Assert.AreEqual("workflow.updateFields", edit.TriggerKey);
        Assert.AreEqual(TriggerDispatchMode.InTransaction, edit.DispatchMode);
        Assert.AreEqual(TriggerFailurePolicy.FailOperation, edit.FailurePolicy);
    }

    [TestMethod]
    public void A_trigger_follows_the_action_it_was_switched_to()
    {
        var edit = new TriggerEditModel();

        TriggerSelection.Apply(edit, Webhook);
        TriggerSelection.Apply(edit, UpdateFields);

        Assert.AreEqual(TriggerDispatchMode.InTransaction, edit.DispatchMode);
        Assert.AreEqual(TriggerFailurePolicy.FailOperation, edit.FailurePolicy);
    }

    [TestMethod]
    public void Switching_an_existing_trigger_to_a_field_update_adopts_its_defaults()
    {
        // A webhook's AfterCommit would leave a refused write nothing to roll back.
        var edit = new TriggerEditModel
        {
            TriggerKey = "workflow.webhook",
            DispatchMode = TriggerDispatchMode.AfterCommit,
            FailurePolicy = TriggerFailurePolicy.LogAndContinue
        };

        TriggerSelection.Apply(edit, UpdateFields);

        Assert.AreEqual("workflow.updateFields", edit.TriggerKey);
        Assert.AreEqual(TriggerDispatchMode.InTransaction, edit.DispatchMode);
        Assert.AreEqual(TriggerFailurePolicy.FailOperation, edit.FailurePolicy);
    }

    [TestMethod]
    public void It_drops_configuration_of_the_previous_action_and_fills_defaults()
    {
        var edit = new TriggerEditModel
        {
            Configuration = new() { ["target"] = "document", ["assignments"] = "[]" }
        };

        TriggerSelection.Apply(edit, Webhook);

        Assert.HasCount(1, edit.Configuration);
        Assert.AreEqual("https://example.test", edit.Configuration["url"]);
    }

    [TestMethod]
    public void It_keeps_configuration_already_set_over_a_parameter_default()
    {
        var edit = new TriggerEditModel { Configuration = new() { ["url"] = "https://mine.test" } };

        TriggerSelection.Apply(edit, Webhook);

        Assert.AreEqual("https://mine.test", edit.Configuration["url"]);
    }

    [TestMethod]
    public void It_moves_the_event_onto_one_the_action_handles()
    {
        var edit = new TriggerEditModel { Event = WorkflowEventKind.TaskCreated };

        TriggerSelection.Apply(edit, Webhook);

        Assert.AreEqual(WorkflowEventKind.TaskCompleted, edit.Event);
    }
}
