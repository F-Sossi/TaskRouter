using System.Text.Json;

using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// The builder's edit models were written to be passed between objects in one process. Sending
/// them over HTTP is a new requirement on them, and the failure mode is quiet: a property JSON
/// cannot populate arrives on the far side as a default value, not as an error. A workflow would
/// simply lose a setting somewhere between the browser and the server.
///
/// <para>Every value below is deliberately <b>not</b> the property's default, so a property that
/// silently fails to round-trip fails a test rather than passing by coincidence.</para>
/// </summary>
[TestClass]
public class BuilderSerializationTests
{
    /// <summary>The options the endpoints and the HTTP client will use.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static readonly Guid TaskLocalId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTaskLocalId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static WorkflowEditModel FullyPopulated() => new()
    {
        DefinitionId = 7,
        VersionId = 9,
        Version = 3,
        IsPublished = true,
        Name = "Document Review",
        SubjectType = "ChangeRequest",
        Description = "Every property set to something that is not its default.",
        IsSubWorkflow = true,
        EntryTaskLocalId = TaskLocalId,
        Tasks =
        [
            new TaskEditModel
            {
                LocalId = TaskLocalId,
                Id = 11,
                TaskTypeKey = "branch-review",
                DisplayName = "Branch Review",
                IsRequired = false,          // defaults true
                IsAdHoc = true,
                IsBlocking = true,
                IsForkable = true,
                IsConvergencePoint = true,
                IsTerminal = true,
                AssignmentRoleKey = "section-lead",
                ReminderLeadTimeMinutes = 2880,
                Outcomes =
                [
                    new OutcomeEditModel
                    {
                        Id = 21,
                        OutcomeKey = "approved",
                        DisplayName = "Approved",
                        Order = 4,
                    },
                ],
                Routes =
                [
                    new RouteEditModel
                    {
                        Id = 31,
                        OutcomeKey = "rejected",
                        NextTaskLocalId = OtherTaskLocalId,
                        ConditionKey = "requires-review",
                        IsDefault = false,   // defaults true
                        IsReworkRoute = true,
                        Order = 5,
                    },
                ],
                Triggers =
                [
                    new TriggerEditModel
                    {
                        Id = 41,
                        TriggerKey = "workflow.notify",
                        Event = WorkflowEventKind.TaskOverdue,
                        CustomEventName = "custom-event",
                        // A null value in the dictionary: trigger configuration is
                        // Dictionary<string, string?>, and a null is a legitimate entry.
                        Configuration = new Dictionary<string, string?>
                        {
                            ["subject"] = "Overdue",
                            ["body"] = null,
                        },
                        Condition = "task.outcome == 'rejected'",
                        Order = 6,
                        IsActive = false,    // defaults true
                        DispatchMode = TriggerDispatchMode.AfterCommit,
                        FailurePolicy = TriggerFailurePolicy.FailOperation,
                    },
                ],
            },
        ],
        SubWorkflows =
        [
            new SubWorkflowAttachmentEditModel
            {
                Id = 51,
                SubWorkflowDefinitionId = 61,
                TaskLocalId = TaskLocalId,
                IsAutomatic = true,
                IsBlocking = false,          // defaults true
                AllowMultiple = true,
            },
        ],
    };

    private static WorkflowEditModel RoundTrip(WorkflowEditModel model) =>
        JsonSerializer.Deserialize<WorkflowEditModel>(
            JsonSerializer.Serialize(model, Options), Options)!;

    [TestMethod]
    public void The_workflow_itself_survives_a_round_trip()
    {
        var original = FullyPopulated();
        var back = RoundTrip(original);

        Assert.AreEqual(original.DefinitionId, back.DefinitionId);
        Assert.AreEqual(original.VersionId, back.VersionId);
        Assert.AreEqual(original.Version, back.Version);
        Assert.AreEqual(original.IsPublished, back.IsPublished);
        Assert.AreEqual(original.Name, back.Name);
        Assert.AreEqual(original.SubjectType, back.SubjectType);
        Assert.AreEqual(original.Description, back.Description);
        Assert.AreEqual(original.IsSubWorkflow, back.IsSubWorkflow);
        Assert.AreEqual(original.EntryTaskLocalId, back.EntryTaskLocalId);
        Assert.HasCount(1, back.Tasks);
        Assert.HasCount(1, back.SubWorkflows);
    }

    [TestMethod]
    public void A_task_survives_a_round_trip()
    {
        var original = FullyPopulated().Tasks[0];
        var back = RoundTrip(FullyPopulated()).Tasks[0];

        Assert.AreEqual(original.LocalId, back.LocalId,
            "The local id is what routes point at; losing it detaches every route.");
        Assert.AreEqual(original.Id, back.Id);
        Assert.AreEqual(original.TaskTypeKey, back.TaskTypeKey);
        Assert.AreEqual(original.DisplayName, back.DisplayName);
        Assert.AreEqual(original.IsRequired, back.IsRequired);
        Assert.AreEqual(original.IsAdHoc, back.IsAdHoc);
        Assert.AreEqual(original.IsBlocking, back.IsBlocking);
        Assert.AreEqual(original.IsForkable, back.IsForkable);
        Assert.AreEqual(original.IsConvergencePoint, back.IsConvergencePoint);
        Assert.AreEqual(original.IsTerminal, back.IsTerminal);
        Assert.AreEqual(original.AssignmentRoleKey, back.AssignmentRoleKey);
    }

    [TestMethod]
    public void The_reminder_lead_time_is_not_corrupted_by_its_own_second_representation()
    {
        // ReminderLeadTimeDays is a computed property over ReminderLeadTimeMinutes with a
        // setter, so BOTH serialize and whichever lands last on deserialization wins. If the
        // two disagree by a rounding error the value drifts every time a workflow is opened
        // and saved -- which nothing in process would ever have shown.
        foreach (var minutes in new[] { 1, 59, 100, 1439, 1440, 2880, 10_081 })
        {
            var model = FullyPopulated();
            model.Tasks[0].ReminderLeadTimeMinutes = minutes;

            var back = RoundTrip(model).Tasks[0];

            Assert.AreEqual(minutes, back.ReminderLeadTimeMinutes,
                $"{minutes} minutes did not survive the round trip intact.");
        }
    }

    [TestMethod]
    public void A_null_reminder_lead_time_stays_null()
    {
        var model = FullyPopulated();
        model.Tasks[0].ReminderLeadTimeMinutes = null;

        var back = RoundTrip(model).Tasks[0];

        Assert.IsNull(back.ReminderLeadTimeMinutes, "Null means this task never nudges.");
        Assert.IsNull(back.ReminderLeadTimeDays);
    }

    [TestMethod]
    public void Outcomes_routes_and_triggers_survive_a_round_trip()
    {
        var original = FullyPopulated().Tasks[0];
        var back = RoundTrip(FullyPopulated()).Tasks[0];

        var outcome = back.Outcomes.Single();
        Assert.AreEqual(original.Outcomes[0].Id, outcome.Id);
        Assert.AreEqual(original.Outcomes[0].OutcomeKey, outcome.OutcomeKey);
        Assert.AreEqual(original.Outcomes[0].DisplayName, outcome.DisplayName);
        Assert.AreEqual(original.Outcomes[0].Order, outcome.Order);

        var route = back.Routes.Single();
        Assert.AreEqual(original.Routes[0].Id, route.Id);
        Assert.AreEqual(original.Routes[0].OutcomeKey, route.OutcomeKey);
        Assert.AreEqual(original.Routes[0].NextTaskLocalId, route.NextTaskLocalId,
            "A route with no target is a dead end the validator would refuse.");
        Assert.AreEqual(original.Routes[0].ConditionKey, route.ConditionKey);
        Assert.AreEqual(original.Routes[0].IsDefault, route.IsDefault);
        Assert.AreEqual(original.Routes[0].IsReworkRoute, route.IsReworkRoute);
        Assert.AreEqual(original.Routes[0].Order, route.Order);

        var trigger = back.Triggers.Single();
        Assert.AreEqual(original.Triggers[0].Id, trigger.Id);
        Assert.AreEqual(original.Triggers[0].TriggerKey, trigger.TriggerKey);
        Assert.AreEqual(original.Triggers[0].Event, trigger.Event);
        Assert.AreEqual(original.Triggers[0].CustomEventName, trigger.CustomEventName);
        Assert.AreEqual(original.Triggers[0].Condition, trigger.Condition);
        Assert.AreEqual(original.Triggers[0].Order, trigger.Order);
        Assert.AreEqual(original.Triggers[0].IsActive, trigger.IsActive);
        Assert.AreEqual(original.Triggers[0].DispatchMode, trigger.DispatchMode);
        Assert.AreEqual(original.Triggers[0].FailurePolicy, trigger.FailurePolicy);
    }

    [TestMethod]
    public void Trigger_configuration_survives_including_its_null_values()
    {
        var back = RoundTrip(FullyPopulated()).Tasks[0].Triggers.Single();

        Assert.HasCount(2, back.Configuration);
        Assert.AreEqual("Overdue", back.Configuration["subject"]);
        Assert.IsTrue(back.Configuration.ContainsKey("body"),
            "A key with a null value must survive as a key, not vanish.");
        Assert.IsNull(back.Configuration["body"]);
    }

    [TestMethod]
    public void A_sub_workflow_attachment_survives_a_round_trip()
    {
        var original = FullyPopulated().SubWorkflows[0];
        var back = RoundTrip(FullyPopulated()).SubWorkflows.Single();

        Assert.AreEqual(original.Id, back.Id);
        Assert.AreEqual(original.SubWorkflowDefinitionId, back.SubWorkflowDefinitionId);
        Assert.AreEqual(original.TaskLocalId, back.TaskLocalId);
        Assert.AreEqual(original.IsAutomatic, back.IsAutomatic);
        Assert.AreEqual(original.IsBlocking, back.IsBlocking);
        Assert.AreEqual(original.AllowMultiple, back.AllowMultiple);
    }

    [TestMethod]
    public void The_wire_carries_both_representations_of_the_reminder_lead_time()
    {
        // Proof that the round-trip test above exercises a real hazard rather than passing
        // vacuously. If ReminderLeadTimeDays ever stops being serialized -- a [JsonIgnore],
        // say -- the ordering risk disappears and so does the reason for that test, and
        // whoever removes it should see this fail and understand why it was there.
        var json = JsonSerializer.Serialize(FullyPopulated(), Options);

        StringAssert.Contains(json, "reminderLeadTimeMinutes");
        StringAssert.Contains(json, "reminderLeadTimeDays",
            "Both representations go over the wire, so deserialization order decides the value.");
    }

    [TestMethod]
    public void A_workflow_survives_being_round_tripped_twice()
    {
        // Open, save, open, save. Any value that drifts by a rounding error rather than
        // failing outright shows up as movement between the first and second pass, which a
        // single round trip can miss.
        var once = RoundTrip(FullyPopulated());
        var twice = RoundTrip(once);

        Assert.AreEqual(
            JsonSerializer.Serialize(once, Options),
            JsonSerializer.Serialize(twice, Options),
            "A second round trip changed something, so the model is not a stable wire format.");
    }
}
