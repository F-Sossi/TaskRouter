using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;

using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Builds a workflow that mirrors the the original system ChangeRequest chain closely enough to be a
/// faithful reference for the real integration:
///
///     Enter Record ──approved──▶ Provide Input ──approved──▶ PM Review ──approved──▶ Close
///                                     │                          │
///                              (forkable by section)      rejected / rework
///                                                                │
///                                                                ▼
///                                                        (re-fork rejected branches)
///
/// Plus an ad-hoc "Provide Input" that carries its own follow-on chain
/// (Section Review → Division Review), which is the sub-workflow shape the original system cannot express
/// today because its routes target a task *type* that may appear only once per workflow.
/// Here Section Review appears twice — once in the mainline, once in the ad-hoc chain —
/// which is legal precisely because routes target definition ids.
/// </summary>
public static class DemoWorkflowSeeder
{
    public const string ChangeRequestWorkflowName = "ChangeRequest Document Review";

    public static class Outcomes
    {
        public const string Approved = "approved";
        public const string Rejected = "rejected";
    }

    public static async Task<int> SeedAsync(DemoDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var existing = await db.WorkflowDefinitions
            .FirstOrDefaultAsync(w => w.Name == ChangeRequestWorkflowName, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            return existing.Id;
        }

        await SeedOrgAsync(db, ct).ConfigureAwait(false);

        const string actor = "system";
        var now = DateTime.UtcNow;

        // ── Task types are data rows, not enum values ──
        var types = new Dictionary<string, TaskTypeDefinition>();
        foreach (var (key, name) in new[]
        {
            ("enter-record", "Enter Record"),
            ("provide-input", "Provide Input"),
            ("get-info", "Get Info"),
            ("section-review", "Section Review"),
            ("division-review", "Division Review"),
            ("pm-review", "PM Review"),
            ("close-document", "Close Document")
        })
        {
            var t = new TaskTypeDefinition
            {
                Key = key,
                DisplayName = name,
                CreatorId = actor,
                ModifierId = actor,
                Created = now,
                Modified = now
            };
            db.WorkflowTaskTypes.Add(t);
            types[key] = t;
        }

        var definition = new WorkflowDefinition
        {
            Name = ChangeRequestWorkflowName,
            Description = "Mirrors the the original system ChangeRequest review chain.",
            CreatorId = actor,
            ModifierId = actor,
            Created = now,
            Modified = now
        };
        db.WorkflowDefinitions.Add(definition);

        var version = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = 0,
            WorkflowDefinition = definition,
            Version = 1,
            SubjectType = nameof(DemoDocumentType.ChangeRequest),
            // Not flagged published here: PublishWithEntryTaskAsync does that at the end,
            // once the graph is complete and has validated.
            CreatorId = actor,
            ModifierId = actor,
            Created = now,
            Modified = now
        };
        db.WorkflowDefinitionVersions.Add(version);

        WorkflowTaskDefinition Task(
            string typeKey,
            string? displayName = null,
            string? role = null,
            bool adHoc = false,
            bool forkable = false,
            bool convergence = false,
            bool terminal = false,
            bool blocking = false,
            int? reminderLeadTimeMinutes = null)
        {
            var d = new WorkflowTaskDefinition
            {
                WorkflowDefinitionVersionId = 0,
                WorkflowDefinitionVersion = version,
                TaskTypeDefinitionId = 0,
                TaskType = types[typeKey],
                DisplayName = displayName,
                AssignmentRoleKey = role,
                IsAdHoc = adHoc,
                IsForkable = forkable,
                IsConvergencePoint = convergence,
                IsTerminal = terminal,
                IsBlocking = blocking,
                ReminderLeadTimeMinutes = reminderLeadTimeMinutes,
                CreatorId = actor,
                ModifierId = actor,
                Created = now,
                Modified = now
            };
            version.Tasks.Add(d);
            db.WorkflowTaskDefinitions.Add(d);
            return d;
        }

        // ── Mainline ──
        var enterRecord = Task("enter-record", "Enter Record", DemoRoles.Originator);
        // Two days, so /inbox has something to show and the sweeper has something to
        // find. Provide Input rather than the entry task on purpose: it is the forkable
        // one, so a forked run produces several reminded tasks and exercises the funnel's
        // fork path in the demo as well as in the tests.
        var provideInput = Task("provide-input", "Provide Input", DemoRoles.SectionLead,
            forkable: true, reminderLeadTimeMinutes: 2880);
        var pmReview = Task("pm-review", "PM Review", DemoRoles.DivisionHead, convergence: true);
        var closeDocument = Task("close-document", "Close Document", terminal: true);

        // ── Ad-hoc sub-chain. Note section-review appears here AND could appear in the
        //    mainline: same task type, two definitions, distinct routes. ──
        var adHocInput = Task("provide-input", "Ad-hoc Provide Input",
            DemoRoles.SectionLead, adHoc: true, blocking: true);
        var adHocSectionLead = Task("section-review", "Ad-hoc Section Review", DemoRoles.SectionLead, adHoc: true);
        var adHocDivision = Task("division-review", "Ad-hoc Division Review",
            DemoRoles.DivisionHead, adHoc: true);

        void Outcome(WorkflowTaskDefinition d, string key, string name, int order)
        {
            var o = new TaskOutcomeDefinition
            {
                TaskDefinitionId = 0,
                TaskDefinition = d,
                OutcomeKey = key,
                DisplayName = name,
                Order = order,
                CreatorId = actor,
                ModifierId = actor,
                Created = now,
                Modified = now
            };
            d.ValidOutcomes.Add(o);
            db.WorkflowTaskOutcomes.Add(o);
        }

        void Route(
            WorkflowTaskDefinition from,
            string outcome,
            WorkflowTaskDefinition to,
            bool rework = false)
        {
            var r = new TaskRoute
            {
                TaskDefinitionId = 0,
                TaskDefinition = from,
                TaskOutcomeDefinitionId = 0,
                Outcome = OutcomeOf(from, outcome),
                NextTaskDefinitionId = 0,
                NextTaskDefinition = to,
                IsDefault = true,
                IsReworkRoute = rework,
                CreatorId = actor,
                ModifierId = actor,
                Created = now,
                Modified = now
            };
            from.OutgoingRoutes.Add(r);
            db.WorkflowTaskRoutes.Add(r);
        }

        foreach (var d in new[] { enterRecord, provideInput, pmReview, adHocInput, adHocSectionLead, adHocDivision })
        {
            Outcome(d, Outcomes.Approved, "Approved", 1);
            Outcome(d, Outcomes.Rejected, "Rejected", 2);
        }

        Route(enterRecord, Outcomes.Approved, provideInput);
        Route(enterRecord, Outcomes.Rejected, closeDocument);

        Route(provideInput, Outcomes.Approved, pmReview);
        Route(provideInput, Outcomes.Rejected, pmReview);

        Route(pmReview, Outcomes.Approved, closeDocument);
        // The rework route: sends rejected branches back to Provide Input.
        Route(pmReview, Outcomes.Rejected, provideInput, rework: true);

        // Ad-hoc chain: Provide Input → Section Review → Division Review
        Route(adHocInput, Outcomes.Approved, adHocSectionLead);
        Route(adHocInput, Outcomes.Rejected, adHocSectionLead);
        Route(adHocSectionLead, Outcomes.Approved, adHocDivision);
        Route(adHocSectionLead, Outcomes.Rejected, adHocDivision);

        // ── Triggers, exercising all three dispatch modes ──
        void Trigger(
            WorkflowTaskDefinition on,
            string key,
            WorkflowEventKind evt,
            string? configJson = null,
            TriggerDispatchMode mode = TriggerDispatchMode.InTransaction,
            string? condition = null,
            int order = 0)
        {
            var t = new TriggerDefinition
            {
                TaskDefinitionId = 0,
                TaskDefinition = on,
                TriggerKey = key,
                Event = evt,
                Configuration = configJson,
                Condition = condition,
                Order = order,
                DispatchMode = mode,
                FailurePolicy = TriggerFailurePolicy.LogAndContinue,
                CreatorId = actor,
                ModifierId = actor,
                Created = now,
                Modified = now
            };
            on.Triggers.Add(t);
            db.WorkflowTriggerDefinitions.Add(t);
        }

        // Progress is recomputed whenever a mainline task completes.
        foreach (var d in new[] { enterRecord, provideInput, pmReview })
        {
            Trigger(d, BuiltInTriggerKeys.ReportProgress, WorkflowEventKind.TaskCompleted,
                """{"scope":"auto"}""", order: 1);
        }

        // Record the last outcome as a run variable, so route conditions can use it.
        Trigger(provideInput, BuiltInTriggerKeys.SetVariable, WorkflowEventKind.TaskCompleted,
            """{"name":"lastInputOutcome","value":"{task.outcome}","scope":"run"}""", order: 2);

        // Notify the assignee when work lands on them — after commit, because a
        // notification cannot be un-sent if the transaction rolls back.
        Trigger(pmReview, BuiltInTriggerKeys.Notify, WorkflowEventKind.TaskCreated,
            """{"subject":"PM Review required","body":"Task {task.id} ({task.name}) is ready for review."}""",
            TriggerDispatchMode.AfterCommit, order: 1);

        // Conditional: only notify on rejection.
        Trigger(pmReview, BuiltInTriggerKeys.Notify, WorkflowEventKind.TaskCompleted,
            """{"subject":"Work rejected","body":"Task {task.id} was rejected."}""",
            TriggerDispatchMode.AfterCommit,
            condition: "task.outcome == 'rejected'",
            order: 2);

        // Escalate when a review goes past its deadline. On provideInput rather than
        // pmReview because of which rung of the org chart holds the task: provideInput is
        // a SectionLead task, so DemoAssignmentResolver assigns it to the section's Section Lead and the
        // escalation goes *up*, to the division head. pmReview is a DivisionHead task --
        // its assignee is already the top of this chart, so the trigger's top-of-ladder
        // guard would return null and send nothing, demonstrating nothing. (provideInput
        // also carries a reminder lead time, so the demo happens to show both halves of a
        // deadline on one task -- but that is a bonus, not the reason.)
        Trigger(provideInput, DemoEscalationTrigger.TriggerKey, WorkflowEventKind.TaskOverdue,
            """{"subject":"Overdue review","body":"Task {task.id} is overdue. Assignee: {task.assignee}"}""",
            TriggerDispatchMode.AfterCommit,
            order: 3);

        // Saves the graph, back-fills the entry task's id, validates, and publishes —
        // in that order. The seeded workflow must validate cleanly with no exemptions:
        // if it needed them, either the workflow or the validator would be wrong.
        var errors = await db.PublishWithEntryTaskAsync(version, enterRecord, ct).ConfigureAwait(false);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Seeded workflow failed validation: " +
                string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}")));
        }

        await SeedSubWorkflowAsync(db, types, provideInput.Id, actor, now, ct).ConfigureAwait(false);

        return definition.Id;
    }

    /// <summary>
    /// A reusable "Technical Review" sub-workflow, attached to Provide Input.
    ///
    /// It is an ordinary workflow definition with IsSubWorkflow set, built from the same
    /// task types the mainline uses — <c>section-review</c> appears here and could appear in
    /// the mainline at the same time, because routes target task definitions rather than
    /// task types. That is the property the whole sub-workflow design rests on, and the
    /// one the original system cannot express.
    ///
    /// Attached rather than copied: hanging it off five mainline tasks would be five
    /// rows, not five copies of the graph.
    /// </summary>
    private static async Task SeedSubWorkflowAsync(
        DemoDbContext db,
        Dictionary<string, TaskTypeDefinition> types,
        int attachToTaskDefinitionId,
        string actor,
        DateTime now,
        CancellationToken ct)
    {
        var definition = new WorkflowDefinition
        {
            Name = "Technical Review",
            Description = "A reusable review chain that can hang off any task.",
            IsSubWorkflow = true,
            CreatorId = actor,
            ModifierId = actor,
            Created = now,
            Modified = now
        };
        db.WorkflowDefinitions.Add(definition);

        var version = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = 0,
            WorkflowDefinition = definition,
            Version = 1,
            CreatorId = actor,
            ModifierId = actor,
            Created = now,
            Modified = now
        };
        db.WorkflowDefinitionVersions.Add(version);

        WorkflowTaskDefinition Task(string typeKey, string displayName, string? role, bool terminal = false)
        {
            var d = new WorkflowTaskDefinition
            {
                WorkflowDefinitionVersionId = 0,
                WorkflowDefinitionVersion = version,
                TaskTypeDefinitionId = 0,
                TaskType = types[typeKey],
                DisplayName = displayName,
                AssignmentRoleKey = role,
                IsTerminal = terminal,
                CreatorId = actor,
                ModifierId = actor,
                Created = now,
                Modified = now
            };
            version.Tasks.Add(d);
            db.WorkflowTaskDefinitions.Add(d);
            return d;
        }

        // role: null — the entry task goes to whoever the chain was delegated to, and the
        // two reviews above it resolve from that person's section. Note this local Task()
        // helper takes the role positionally; it is not the one the mainline seeding uses.
        var getInfo = Task("get-info", "Technical Review — Get Info", null);
        var sectionLead = Task("section-review", "Technical Review — Section Lead", DemoRoles.SectionLead);
        var divisionReview = Task("division-review", "Technical Review — Division", DemoRoles.DivisionHead, terminal: true);

        foreach (var (task, key, name, order) in new[]
        {
            (getInfo, Outcomes.Approved, "Provided", 1),
            (sectionLead, Outcomes.Approved, "Approved", 1),
            (sectionLead, Outcomes.Rejected, "Rejected", 2),
            (divisionReview, Outcomes.Approved, "Approved", 1),
            (divisionReview, Outcomes.Rejected, "Rejected", 2)
        })
        {
            var o = new TaskOutcomeDefinition
            {
                TaskDefinitionId = 0,
                TaskDefinition = task,
                OutcomeKey = key,
                DisplayName = name,
                Order = order,
                CreatorId = actor,
                ModifierId = actor,
                Created = now,
                Modified = now
            };
            task.ValidOutcomes.Add(o);
            db.WorkflowTaskOutcomes.Add(o);
        }

        var getInfoRoute = new TaskRoute
        {
            TaskDefinitionId = 0,
            TaskDefinition = getInfo,
            TaskOutcomeDefinitionId = 0,
            Outcome = OutcomeOf(getInfo, Outcomes.Approved),
            NextTaskDefinitionId = 0,
            NextTaskDefinition = sectionLead,
            IsDefault = true,
            Order = 1,
            CreatorId = actor,
            ModifierId = actor,
            Created = now,
            Modified = now
        };
        getInfo.OutgoingRoutes.Add(getInfoRoute);
        db.WorkflowTaskRoutes.Add(getInfoRoute);

        foreach (var outcome in new[] { Outcomes.Approved, Outcomes.Rejected })
        {
            var r = new TaskRoute
            {
                TaskDefinitionId = 0,
                TaskDefinition = sectionLead,
                TaskOutcomeDefinitionId = 0,
                Outcome = OutcomeOf(sectionLead, outcome),
                NextTaskDefinitionId = 0,
                NextTaskDefinition = divisionReview,
                IsDefault = true,
                CreatorId = actor,
                ModifierId = actor,
                Created = now,
                Modified = now
            };
            sectionLead.OutgoingRoutes.Add(r);
            db.WorkflowTaskRoutes.Add(r);
        }

        // The attachment is scoped by the mainline version, which is the one the task
        // it hangs off belongs to.
        var mainlineVersionId = await db.WorkflowTaskDefinitions
            .Where(d => d.Id == attachToTaskDefinitionId)
            .Select(d => d.WorkflowDefinitionVersionId)
            .SingleAsync(ct).ConfigureAwait(false);

        // On request rather than automatic, and blocking: Provide Input cannot complete
        // while a technical review it started is still going.
        db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
        {
            // By navigation, not by id: this definition is still unsaved, and its id is
            // minted by the same save PublishWithEntryTaskAsync does below.
            SubWorkflowDefinitionId = 0,
            SubWorkflowDefinition = definition,
            WorkflowDefinitionVersionId = mainlineVersionId,
            TaskDefinitionId = attachToTaskDefinitionId,
            IsAutomatic = false,
            IsBlocking = true,
            AllowMultiple = false,
            CreatorId = actor,
            ModifierId = actor,
            Created = now,
            Modified = now
        });

        // Held to the same standard as the mainline: a sub-workflow is a workflow.
        var errors = await db.PublishWithEntryTaskAsync(version, getInfo, ct).ConfigureAwait(false);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Seeded sub-workflow failed validation: " +
                string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}")));
        }
    }

    private static async Task SeedOrgAsync(DemoDbContext db, CancellationToken ct)
    {
        if (await db.Sections.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var division = new Division { Name = "Engineering Division", DivisionHeadActorId = "user-division-head" };
        db.Divisions.Add(division);
        db.Groups.Add(new Group { Name = "CVN-74 Availability" });

        db.Sections.AddRange(
            new Section { Code = "C100", Name = "Electrical", SectionLeadActorId = "user-lead-c100", Division = division },
            new Section { Code = "C200", Name = "Mechanical", SectionLeadActorId = "user-lead-c200", Division = division },
            new Section { Code = "C300", Name = "Structural", SectionLeadActorId = "user-lead-c300", Division = division });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var byCode = await db.Sections.ToDictionaryAsync(s => s.Code, s => s.Id, ct)
            .ConfigureAwait(false);

        db.People.AddRange(
            new Person { ActorId = "user-originator", FullName = "Pat Originator" },
            new Person { ActorId = "user-division-head", FullName = "Robin Division Head" },

            // Section Leads belong to the section they lead, so delegating to a Section Lead resolves
            // to themselves rather than escaping to another section.
            new Person { ActorId = "user-lead-c100", FullName = "Alex Section Lead (Electrical)", SectionId = byCode["C100"] },
            new Person { ActorId = "user-lead-c200", FullName = "Sam Section Lead (Mechanical)", SectionId = byCode["C200"] },
            new Person { ActorId = "user-lead-c300", FullName = "Jo Section Lead (Structural)", SectionId = byCode["C300"] },

            // Ordinary workers — the people a chain is actually delegated to.
            new Person { ActorId = "user-worker-c100", FullName = "Casey Ellis (Electrical)", SectionId = byCode["C100"] },
            new Person { ActorId = "user-worker-c200", FullName = "Drew Novak (Mechanical)", SectionId = byCode["C200"] },
            new Person { ActorId = "user-worker-c300", FullName = "Ari Benn (Structural)", SectionId = byCode["C300"] },

            // Deliberately sectionless: the degraded path has to stay exercised.
            new Person { ActorId = "user-unassigned", FullName = "Sky Vance (no section)" });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The outcome row a task declares under this key.
    ///
    /// <para>Routes point at an outcome row rather than naming its key, so a seeder has to
    /// find the row it already created. By reference, because none of these has a database id
    /// until the save — the same reason the rest of this seeder wires navigation properties
    /// rather than ids.</para>
    /// </summary>
    private static TaskOutcomeDefinition OutcomeOf(WorkflowTaskDefinition task, string outcomeKey) =>
        task.ValidOutcomes.FirstOrDefault(o =>
            string.Equals(o.OutcomeKey, outcomeKey, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"'{task.DisplayName}' declares no outcome '{outcomeKey}', so nothing can route on it.");
}
