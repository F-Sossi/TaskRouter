using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Clone and CopyFrom exist for one reason: the edit dialogs bind to a copy so that
/// Cancel means cancel.
///
/// Blazor passes reference types by reference, so the dialogs originally bound straight
/// to the caller's object and every keystroke landed on it immediately — Cancel closed
/// the dialog with all the changes already applied. These tests pin the copy semantics
/// that fix it. The dialogs themselves are not covered; that needs bUnit, which is
/// deliberately deferred while the UI is still moving.
/// </summary>
[TestClass]
public class EditModelCloneTests
{
    [TestMethod]
    public void Cloning_a_task_shares_nothing_mutable_with_the_original()
    {
        var original = new TaskEditModel { TaskTypeKey = "review", DisplayName = "Review" };
        original.Outcomes.Add(new OutcomeEditModel { OutcomeKey = "approved", DisplayName = "Approved" });
        original.Routes.Add(new RouteEditModel { OutcomeKey = "approved", NextTaskLocalId = Guid.NewGuid() });
        original.Triggers.Add(new TriggerEditModel
        {
            TriggerKey = "workflow.notify",
            Configuration = new Dictionary<string, string?> { ["to"] = "someone" }
        });

        var copy = original.Clone();

        // Everything the dialog can touch must be discardable.
        copy.DisplayName = "Changed";
        copy.IsTerminal = true;
        copy.Outcomes.Add(new OutcomeEditModel { OutcomeKey = "rejected", DisplayName = "Rejected" });
        copy.Outcomes[0].DisplayName = "Mutated";
        copy.Triggers[0].Configuration["to"] = "someone else";
        copy.Routes[0].OutcomeKey = "rejected";

        Assert.AreEqual("Review", original.DisplayName);
        Assert.IsFalse(original.IsTerminal);
        Assert.HasCount(1, original.Outcomes);
        Assert.AreEqual("Approved", original.Outcomes[0].DisplayName);
        Assert.AreEqual("someone", original.Triggers[0].Configuration["to"]);
        Assert.AreEqual("approved", original.Routes[0].OutcomeKey);
    }

    [TestMethod]
    public void A_clone_keeps_the_local_id_because_routes_point_at_it()
    {
        var task = new TaskEditModel { TaskTypeKey = "review" };

        Assert.AreEqual(task.LocalId, task.Clone().LocalId);
    }

    [TestMethod]
    public void CopyFrom_commits_the_edit_without_replacing_the_object()
    {
        var task = new TaskEditModel { TaskTypeKey = "review", DisplayName = "Review" };
        var identity = task.LocalId;

        var edited = task.Clone();
        edited.DisplayName = "Reviewed by Section Lead";
        edited.IsForkable = true;
        edited.Outcomes.Add(new OutcomeEditModel { OutcomeKey = "approved", DisplayName = "Approved" });

        task.CopyFrom(edited);

        Assert.AreEqual("Reviewed by Section Lead", task.DisplayName);
        Assert.IsTrue(task.IsForkable);
        Assert.HasCount(1, task.Outcomes);

        // The caller holds this reference and routes target this id; neither may change.
        Assert.AreEqual(identity, task.LocalId);
    }

    [TestMethod]
    public void CopyFrom_does_not_leave_the_committed_copy_aliased()
    {
        var task = new TaskEditModel { TaskTypeKey = "review" };

        var edited = task.Clone();
        edited.Outcomes.Add(new OutcomeEditModel { OutcomeKey = "approved", DisplayName = "Approved" });

        task.CopyFrom(edited);

        // Reopening the dialog clones again; the previous copy must be inert.
        edited.Outcomes[0].DisplayName = "Leaked";

        Assert.AreEqual("Approved", task.Outcomes[0].DisplayName);
    }

    [TestMethod]
    public void A_trigger_clone_detaches_its_configuration()
    {
        // The trigger dialog rewrites Configuration destructively when the selected
        // trigger changes, so a shared dictionary could not be put back on Cancel.
        var original = new TriggerEditModel
        {
            TriggerKey = "workflow.webhook",
            Event = WorkflowEventKind.TaskCompleted,
            Configuration = new Dictionary<string, string?> { ["url"] = "https://example.test/hook" }
        };

        var copy = original.Clone();
        copy.Configuration.Clear();
        copy.Configuration["name"] = "somethingElse";
        copy.TriggerKey = "workflow.setVariable";

        Assert.AreEqual("workflow.webhook", original.TriggerKey);
        Assert.AreEqual("https://example.test/hook", original.Configuration["url"]);
    }

    [TestMethod]
    public void A_route_clone_is_independent()
    {
        var target = Guid.NewGuid();
        var original = new RouteEditModel { OutcomeKey = "approved", NextTaskLocalId = target };

        var copy = original.Clone();
        copy.OutcomeKey = "rejected";
        copy.NextTaskLocalId = Guid.NewGuid();
        copy.IsReworkRoute = true;

        Assert.AreEqual("approved", original.OutcomeKey);
        Assert.AreEqual(target, original.NextTaskLocalId);
        Assert.IsFalse(original.IsReworkRoute);
    }

    [TestMethod]
    public void Editing_a_cloned_attachment_leaves_the_original_alone()
    {
        var original = new SubWorkflowAttachmentEditModel
        {
            SubWorkflowDefinitionId = 7,
            TaskLocalId = null,
            IsAutomatic = false,
            IsBlocking = true,
            AllowMultiple = false
        };

        var clone = original.Clone();
        clone.IsBlocking = false;
        clone.SubWorkflowDefinitionId = 9;

        Assert.IsTrue(original.IsBlocking);
        Assert.AreEqual(7, original.SubWorkflowDefinitionId);

        original.CopyFrom(clone);

        Assert.IsFalse(original.IsBlocking);
        Assert.AreEqual(9, original.SubWorkflowDefinitionId);
    }

    [TestMethod]
    public void Clone_carries_the_reminder_lead_time()
    {
        var original = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = 2880 };

        var clone = original.Clone();

        Assert.AreEqual(2880, clone.ReminderLeadTimeMinutes);
    }

    [TestMethod]
    public void CopyFrom_carries_the_reminder_lead_time()
    {
        var target = new TaskEditModel { TaskTypeKey = "review" };
        var source = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = 720 };

        target.CopyFrom(source);

        Assert.AreEqual(720, target.ReminderLeadTimeMinutes);
    }

    [TestMethod]
    public void CopyFrom_clears_a_lead_time_the_editor_removed()
    {
        // Cancel means cancel, and so does clearing a field. A CopyFrom that only assigns
        // non-null values would make the field impossible to unset.
        var target = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = 720 };
        var source = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = null };

        target.CopyFrom(source);

        Assert.IsNull(target.ReminderLeadTimeMinutes);
    }

    [TestMethod]
    public void The_day_facing_property_round_trips_a_half_day()
    {
        // The reason this is double? and not int?: an int? getter does integer division on
        // the stored minutes, so a 12-hour lead time reads back as 0 -- a value that is
        // not what is stored, and that the next save would write back as zero.
        var model = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeDays = 0.5 };

        Assert.AreEqual(720, model.ReminderLeadTimeMinutes);
        Assert.AreEqual(0.5, model.ReminderLeadTimeDays);
    }

    [TestMethod]
    public void Clearing_the_day_facing_property_clears_the_minutes()
    {
        var model = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = 2880 };

        model.ReminderLeadTimeDays = null;

        Assert.IsNull(model.ReminderLeadTimeMinutes);
    }
}
