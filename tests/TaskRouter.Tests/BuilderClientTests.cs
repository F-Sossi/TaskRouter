using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;
using TaskRouter.Core.Validation;
using TaskRouter.EntityFrameworkCore.Builder;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// Exercises the builder UI's server half.
///
/// The components themselves are only worth trusting if the thing behind
/// <see cref="IWorkflowBuilderClient"/> round-trips a graph faithfully and refuses to
/// break the versioning guarantee. That guarantee is the whole reason versions exist:
/// a run pins to a version id, so editing a published version would silently reroute
/// work already in flight — the exact the original system defect the design set out to remove.
/// </summary>
[TestClass]
public class BuilderClientTests
{
    private TestHost _host = null!;
    private EfWorkflowBuilderClient _client = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();

        _client = new EfWorkflowBuilderClient(
            _host.Db,
            new WorkflowTriggerRegistry(
            [
                new SetVariableTrigger(),
                new ReportProgressTrigger(),
                new NotifyTrigger(),
                new WebhookTrigger(),
                // Not a built-in: the demo host registers this one itself, and the
                // seeder puts it on Provide Input. A registry missing it makes the
                // builder reject the *seeded* graph with TRIGGER_UNKNOWN, which is the
                // validator working correctly on a fixture that lied about the host.
                new DemoEscalationTrigger(_host.Db, NullLogger<DemoEscalationTrigger>.Instance)
            ]),
            [new RequiresReviewCondition()],
            new WorkflowBuilderOptions
            {
                AssignmentRoles =
                    [DemoRoles.SectionLead, DemoRoles.DivisionHead, DemoRoles.Originator],

                // Outcomes deliberately unset: it narrows the catalogue rather than being
                // the catalogue, and most hosts have nothing to narrow. The one test about
                // narrowing builds its own client with it.
            },
            new TestEditorActorAccessor());
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<int> SeededVersionIdAsync() =>
        await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.IsPublished && v.IsLatest && !v.WorkflowDefinition!.IsSubWorkflow)
            .Select(v => v.Id)
            .SingleAsync();

    // ─────────────────────────────── Round trip ───────────────────────────────

    [TestMethod]
    public async Task GetWorkflow_maps_the_whole_graph_onto_client_side_ids()
    {
        var model = await _client.GetWorkflowAsync(await SeededVersionIdAsync());

        Assert.IsNotNull(model);
        Assert.AreEqual(DemoWorkflowSeeder.ChangeRequestWorkflowName, model.Name);
        Assert.IsTrue(model.IsPublished);
        Assert.HasCount(7, model.Tasks);

        // The entry point survives the int -> Guid translation.
        var entry = model.Find(model.EntryTaskLocalId!.Value);
        Assert.IsNotNull(entry);
        Assert.AreEqual("enter-record", entry.TaskTypeKey);

        // Every route resolves to a task in the same model. A route that mapped to a
        // stale or foreign id would leave a dangling Guid here.
        foreach (var route in model.Tasks.SelectMany(t => t.Routes))
        {
            Assert.IsNotNull(route.NextTaskLocalId, $"route '{route.OutcomeKey}' lost its target");
            Assert.IsNotNull(model.Find(route.NextTaskLocalId.Value));
        }

        // The same task type appears twice, which is only expressible because routes
        // target task definitions rather than task types.
        Assert.AreEqual(2, model.Tasks.Count(t => t.TaskTypeKey == "provide-input"));

        // Trigger configuration comes back as parsed values, not raw JSON.
        var progress = model.Tasks
            .SelectMany(t => t.Triggers)
            .First(t => t.TriggerKey == BuiltInTriggerKeys.ReportProgress);

        Assert.AreEqual("auto", progress.Configuration["scope"]);
    }

    [TestMethod]
    public async Task Blank_version_id_opens_a_new_workflow_rather_than_returning_null()
    {
        var model = await _client.GetWorkflowAsync(0);

        Assert.IsNotNull(model);
        Assert.AreEqual(0, model.DefinitionId);
        Assert.IsEmpty(model.Tasks);
    }

    // ───────────────────────── The versioning guarantee ─────────────────────────

    [TestMethod]
    public async Task Saving_over_a_published_version_forks_a_draft_and_leaves_it_untouched()
    {
        var publishedId = await SeededVersionIdAsync();
        var model = (await _client.GetWorkflowAsync(publishedId))!;

        var before = model.Tasks.Count;
        model.Tasks.Add(new TaskEditModel { TaskTypeKey = "extra-step", IsTerminal = true });

        var result = await _client.SaveAsync(model);

        Assert.IsTrue(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.AreNotEqual(publishedId, result.VersionId, "the published version was edited in place");

        var published = await _host.Db.WorkflowDefinitionVersions
            .Include(v => v.Tasks).AsNoTracking().SingleAsync(v => v.Id == publishedId);

        Assert.HasCount(before, published.Tasks, "the published graph changed");
        Assert.IsTrue(published.IsPublished);
        Assert.IsTrue(published.IsLatest, "the published version stopped being the one runs start on");

        var draft = await _host.Db.WorkflowDefinitionVersions
            .AsNoTracking().SingleAsync(v => v.Id == result.VersionId);

        Assert.IsFalse(draft.IsPublished);
        Assert.IsFalse(draft.IsLatest);
        Assert.AreEqual(published.Version + 1, draft.Version);

        var draftTasks = await _host.Db.WorkflowTaskDefinitions
            .CountAsync(t => t.WorkflowDefinitionVersionId == draft.Id);

        Assert.AreEqual(before + 1, draftTasks, "the edit did not reach the draft");
    }

    [TestMethod]
    public async Task A_run_started_before_a_publish_stays_on_the_version_it_started_with()
    {
        var originalId = await SeededVersionIdAsync();
        var definitionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.Id == originalId).Select(v => v.WorkflowDefinitionId).SingleAsync();

        var inFlight = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-a")).Unwrap();

        // Publish a changed version while that run is live.
        var model = (await _client.GetWorkflowAsync(originalId))!;
        model.Description = "second edition";

        var published = await _client.PublishAsync(model);
        Assert.IsTrue(published.Success, string.Join("; ", published.Errors.Select(e => e.Message)));
        Assert.AreNotEqual(originalId, published.VersionId);

        var run = await _host.Db.WorkflowRuns.AsNoTracking()
            .SingleAsync(r => r.Id == inFlight.Id);

        Assert.AreEqual(originalId, run.WorkflowDefinitionVersionId,
            "an in-flight run was repointed at the new version");

        // And exactly one version is now the one new runs use, which StartRunAsync
        // requires: it selects on IsPublished && IsLatest with SingleOrDefault.
        var latest = await _host.Db.WorkflowDefinitionVersions
            .CountAsync(v => v.WorkflowDefinitionId == definitionId && v.IsPublished && v.IsLatest);

        Assert.AreEqual(1, latest);

        var next = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "2"), definitionId, "user-a")).Unwrap();

        Assert.AreEqual(published.VersionId, next.WorkflowDefinitionVersionId);
    }

    [TestMethod]
    public async Task Subject_type_is_versioned_so_a_draft_cannot_change_what_the_published_version_answers_to()
    {
        var publishedId = await SeededVersionIdAsync();
        var model = (await _client.GetWorkflowAsync(publishedId))!;

        Assert.AreEqual("ChangeRequest", model.SubjectType);

        // Someone experimenting in a draft points it at a different document type.
        model.SubjectType = "Drawing";

        var draft = await _client.SaveAsync(model);
        Assert.IsTrue(draft.Success, string.Join("; ", draft.Errors.Select(e => e.Message)));

        var published = await _host.Db.WorkflowDefinitionVersions
            .AsNoTracking().SingleAsync(v => v.Id == publishedId);

        Assert.AreEqual("ChangeRequest", published.SubjectType,
            "a draft edit changed which documents the published workflow starts for");

        var saved = await _host.Db.WorkflowDefinitionVersions
            .AsNoTracking().SingleAsync(v => v.Id == draft.VersionId);

        Assert.AreEqual("Drawing", saved.SubjectType);

        // The host resolves a workflow through the published version, so starting a ChangeRequest
        // still finds this workflow.
        var svc = new DocumentTaskService(_host.Db, _host.Engine);
        var changeRequest = new ChangeRequest
        {
            Title = "Still routable", DocNumber = "CR-9", Originator = "u", CreatorId = "u"
        };
        _host.Db.Documents.Add(changeRequest);
        await _host.Db.SaveChangesAsync();

        var started = await svc.CreateInitialTasksAsync(changeRequest.Id, DemoDocumentType.ChangeRequest, "u");

        Assert.IsTrue(started.IsOk, started.IsError ? started.UnwrapError().Message : null);
    }

    [TestMethod]
    public async Task Name_and_description_are_deliberately_shared_by_every_version()
    {
        var publishedId = await SeededVersionIdAsync();
        var model = (await _client.GetWorkflowAsync(publishedId))!;

        model.Name = "ChangeRequest Review (renamed)";

        var draft = await _client.SaveAsync(model);
        Assert.IsTrue(draft.Success);

        // Not a bug: the name identifies the logical workflow, not one revision of it.
        // Pinned so that if it ever moves onto the version, it is a decision rather than
        // an accident.
        var published = (await _client.GetWorkflowAsync(publishedId))!;
        Assert.AreEqual("ChangeRequest Review (renamed)", published.Name);
    }

    // ──────────────────────── Draft tolerance, publish rigour ────────────────────────

    [TestMethod]
    public async Task An_incomplete_graph_saves_as_a_draft_but_will_not_publish()
    {
        var model = new WorkflowEditModel { Name = "Half finished" };
        var task = new TaskEditModel { TaskTypeKey = "review", DisplayName = "Review" };
        task.Outcomes.Add(new OutcomeEditModel { OutcomeKey = "approved", DisplayName = "Approved" });
        model.Tasks.Add(task);
        // No entry task, and the one task is neither terminal nor routed anywhere.

        var saved = await _client.SaveAsync(model);
        Assert.IsTrue(saved.Success, "a work-in-progress draft should still be saveable");

        var reloaded = (await _client.GetWorkflowAsync(saved.VersionId))!;
        var publish = await _client.PublishAsync(reloaded);

        Assert.IsFalse(publish.Success);
        Assert.IsNotEmpty(publish.Errors);

        var stillDraft = await _host.Db.WorkflowDefinitionVersions
            .AsNoTracking().SingleAsync(v => v.Id == saved.VersionId);

        Assert.IsFalse(stillDraft.IsPublished);
    }

    [TestMethod]
    public async Task A_route_pointing_nowhere_blocks_even_a_draft_save()
    {
        var model = new WorkflowEditModel { Name = "Broken" };
        var task = new TaskEditModel { TaskTypeKey = "review" };
        task.Routes.Add(new RouteEditModel
        {
            OutcomeKey = "approved",
            NextTaskLocalId = Guid.NewGuid()   // no such task
        });
        model.Tasks.Add(task);

        var result = await _client.SaveAsync(model);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Code == "ROUTE_NO_TARGET"));
    }

    [TestMethod]
    public async Task A_misconfigured_trigger_is_rejected_when_it_is_saved()
    {
        var model = new WorkflowEditModel { Name = "Bad trigger" };
        var task = new TaskEditModel { TaskTypeKey = "review", IsTerminal = true };
        task.Triggers.Add(new TriggerEditModel
        {
            TriggerKey = BuiltInTriggerKeys.Webhook,
            Event = WorkflowEventKind.TaskCompleted,
            Configuration = new Dictionary<string, string?> { ["url"] = "not a url" }
        });
        model.Tasks.Add(task);
        model.EntryTaskLocalId = task.LocalId;

        var result = await _client.SaveAsync(model);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Code == "TRIGGER_CONFIG"));
    }

    // ──────────────────────────── Building from nothing ────────────────────────────

    [TestMethod]
    public async Task A_workflow_built_from_scratch_publishes_and_runs()
    {
        var model = new WorkflowEditModel { Name = "Built in the UI", SubjectType = "ChangeRequest" };

        var review = new TaskEditModel
        {
            TaskTypeKey = "ui-review",
            DisplayName = "Review",
            AssignmentRoleKey = DemoRoles.SectionLead
        };
        review.Outcomes.Add(new OutcomeEditModel { OutcomeKey = "approved", DisplayName = "Approved" });

        var close = new TaskEditModel
        {
            TaskTypeKey = "ui-close",
            DisplayName = "Close",
            IsTerminal = true
        };

        review.Routes.Add(new RouteEditModel { OutcomeKey = "approved", NextTaskLocalId = close.LocalId });

        model.Tasks.Add(review);
        model.Tasks.Add(close);
        model.EntryTaskLocalId = review.LocalId;

        var result = await _client.PublishAsync(model);
        Assert.IsTrue(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));

        // The task types the model invented became rows, which is the point of task
        // types being data rather than an enum.
        Assert.IsTrue(await _host.Db.WorkflowTaskTypes.AnyAsync(t => t.Key == "ui-review"));

        var version = await _host.Db.WorkflowDefinitionVersions
            .Include(v => v.Tasks).AsNoTracking().SingleAsync(v => v.Id == result.VersionId);

        Assert.AreEqual("ChangeRequest", version.SubjectType);
        Assert.IsNotNull(version.EntryTaskDefinitionId);
        Assert.IsTrue(version.Tasks.Any(t => t.Id == version.EntryTaskDefinitionId));

        var run = await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "9"), version.WorkflowDefinitionId, "user-a");

        Assert.IsTrue(run.IsOk, run.IsError ? run.UnwrapError().Message : null);
    }

    [TestMethod]
    public async Task Editing_a_draft_rewrites_it_in_place_rather_than_piling_up_versions()
    {
        var first = await _client.SaveAsync(new WorkflowEditModel
        {
            Name = "Draft",
            Tasks = { new TaskEditModel { TaskTypeKey = "step-one", IsTerminal = true } }
        });

        Assert.IsTrue(first.Success);

        var model = (await _client.GetWorkflowAsync(first.VersionId))!;
        model.Tasks.Add(new TaskEditModel { TaskTypeKey = "step-two", IsTerminal = true });

        var second = await _client.SaveAsync(model);

        Assert.IsTrue(second.Success);
        Assert.AreEqual(first.VersionId, second.VersionId, "editing a draft created a new version");

        var reloaded = (await _client.GetWorkflowAsync(second.VersionId))!;
        Assert.HasCount(2, reloaded.Tasks);

        // The rewrite must not leave the previous graph behind.
        var orphans = await _host.Db.WorkflowTaskDefinitions
            .CountAsync(t => t.WorkflowDefinitionVersionId == second.VersionId);

        Assert.AreEqual(2, orphans);
    }

    // ──────────────────────────────── Host metadata ────────────────────────────────

    [TestMethod]
    public async Task The_builder_is_told_what_the_host_can_resolve()
    {
        var roles = await _client.GetAssignmentRolesAsync();
        var conditions = await _client.GetRouteConditionsAsync();
        var descriptors = await _client.GetTriggerDescriptorsAsync();
        var outcomes = await _client.GetOutcomeTypesAsync();

        CollectionAssert.Contains(roles.ToList(), DemoRoles.SectionLead);
        CollectionAssert.Contains(conditions.ToList(), "requires-review");

        // The catalogue starts empty -- these are rows, and this fixture defines none. An
        // author gets a free-text field until somebody defines one, which is the honest
        // state rather than a broken one.
        Assert.IsEmpty(outcomes);

        // Descriptor-driven config forms only work if every trigger describes itself.
        // Four built-ins plus the host's own escalation -- the count is the registry the
        // fixture was built with, not a claim about how many triggers the library ships.
        Assert.HasCount(5, descriptors);
        Assert.IsTrue(descriptors.All(d => !string.IsNullOrWhiteSpace(d.DisplayName)));
        Assert.IsTrue(descriptors.Any(d => d.Key == BuiltInTriggerKeys.Webhook && d.Parameters.Count > 0));

        // A host trigger is offered exactly like a built-in -- that is the whole point of
        // keying the registry by string. But it declares only TaskOverdue, so the event
        // picker cannot offer an escalation on an event where it would mean nothing.
        var escalate = descriptors.Single(d => d.Key == DemoEscalationTrigger.TriggerKey);
        Assert.AreEqual(TriggerDispatchMode.AfterCommit, escalate.DefaultDispatch);
        CollectionAssert.AreEqual(
            new[] { WorkflowEventKind.TaskOverdue }, escalate.SupportedEvents.ToArray());
    }

    [TestMethod]
    public async Task Only_definitions_flagged_as_sub_workflows_are_offered_for_attachment()
    {
        var options = await _client.GetSubWorkflowDefinitionsAsync();

        var technicalReview = options.SingleOrDefault(o => o.Name == "Technical Review");
        Assert.IsNotNull(technicalReview, "the seeded sub-workflow should be offered");
        Assert.IsTrue(technicalReview.HasPublishedVersion);

        Assert.IsFalse(options.Any(o => o.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName),
            "the mainline workflow is not a sub-workflow and must not appear");
    }

    [TestMethod]
    public async Task CreateDraftVersion_copies_a_published_graph_without_touching_it()
    {
        var publishedId = await SeededVersionIdAsync();

        var draftId = await _client.CreateDraftVersionAsync(publishedId);
        Assert.AreNotEqual(publishedId, draftId);

        var original = (await _client.GetWorkflowAsync(publishedId))!;
        var copy = (await _client.GetWorkflowAsync(draftId))!;

        Assert.AreEqual(original.DefinitionId, copy.DefinitionId);
        Assert.HasCount(original.Tasks.Count, copy.Tasks);
        Assert.IsFalse(copy.IsPublished);
        Assert.IsTrue(original.IsPublished);

        Assert.AreEqual(
            original.Tasks.SelectMany(t => t.Routes).Count(),
            copy.Tasks.SelectMany(t => t.Routes).Count());

        // Task definition ids are freshly minted; a copy that shared them would let an
        // edit to the draft reach tasks the published version's runs depend on.
        var originalIds = original.Tasks.Select(t => t.Id).ToHashSet();
        Assert.IsFalse(copy.Tasks.Any(t => originalIds.Contains(t.Id)));
    }

    // ──────────────────────── Sub-workflow attachment scope ────────────────────────

    // WorkflowDefinitionValidator.Validate early-returns WF_EMPTY for a version with no
    // tasks, before any attachment rule runs. A single terminal task that is also the
    // entry point satisfies every other rule (ValidateOutcomeCoverage and DEAD_END only
    // look at non-terminal tasks, and a self-referencing entry/terminal is trivially
    // reachable), so attachment errors below are the only ones that can appear.
    private static WorkflowDefinitionVersion MinimalPublishableVersion(int taskId = 1)
    {
        var task = new WorkflowTaskDefinition
        {
            Id = taskId,
            WorkflowDefinitionVersionId = 1,
            TaskTypeDefinitionId = 1,
            IsTerminal = true
        };

        return new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = 1,
            Version = 1,
            EntryTaskDefinitionId = task.Id,
            Tasks = [task]
        };
    }

    [TestMethod]
    public void An_automatic_attachment_must_name_a_task()
    {
        var version = MinimalPublishableVersion();
        version.SubWorkflowAttachments =
        [
            new SubWorkflowAttachment
            {
                SubWorkflowDefinitionId = 2,
                WorkflowDefinitionVersionId = 1,
                TaskDefinitionId = null,
                IsAutomatic = true
            }
        ];

        var errors = WorkflowDefinitionValidator.Validate(version);

        Assert.IsTrue(errors.Any(e => e.Code == "attachment.automatic-needs-task"),
            "Automatic and version-wide would spawn an instance on every task in the run.");
    }

    [TestMethod]
    public void An_attachment_naming_an_unknown_task_is_rejected()
    {
        var version = MinimalPublishableVersion();
        version.SubWorkflowAttachments =
        [
            new SubWorkflowAttachment
            {
                SubWorkflowDefinitionId = 2,
                WorkflowDefinitionVersionId = 1,
                TaskDefinitionId = 999,   // not in this version
                IsAutomatic = false
            }
        ];

        var errors = WorkflowDefinitionValidator.Validate(version);

        Assert.IsTrue(errors.Any(e => e.Code == "attachment.unknown-task"),
            "The attachment names a task definition that is not part of this version.");
    }

    [TestMethod]
    public void The_same_sub_workflow_attached_twice_at_the_same_scope_is_rejected()
    {
        var version = MinimalPublishableVersion();
        version.SubWorkflowAttachments =
        [
            new SubWorkflowAttachment
            {
                SubWorkflowDefinitionId = 2,
                WorkflowDefinitionVersionId = 1,
                TaskDefinitionId = null,
                IsAutomatic = false
            },
            new SubWorkflowAttachment
            {
                SubWorkflowDefinitionId = 2,
                WorkflowDefinitionVersionId = 1,
                TaskDefinitionId = null,
                IsAutomatic = false
            }
        ];

        var errors = WorkflowDefinitionValidator.Validate(version);

        Assert.IsTrue(errors.Any(e => e.Code == "attachment.duplicate"),
            "The same sub-workflow attached twice at the same scope should be flagged. " +
            "Use AllowMultiple for concurrent instances instead.");
    }

    [TestMethod]
    public void A_valid_attachment_produces_no_attachment_errors()
    {
        var version = MinimalPublishableVersion();
        version.SubWorkflowAttachments =
        [
            new SubWorkflowAttachment
            {
                SubWorkflowDefinitionId = 2,
                WorkflowDefinitionVersionId = 1,
                TaskDefinitionId = version.Tasks.Single().Id,
                IsAutomatic = true
            }
        ];

        var errors = WorkflowDefinitionValidator.Validate(version);

        Assert.IsFalse(errors.Any(e => e.Code.StartsWith("attachment.")),
            string.Join("; ", errors.Select(e => e.Message)));
    }

    // ──────────────────── Attachments reach the validator ────────────────────

    private async Task<int> SubWorkflowDefinitionIdAsync() =>
        await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

    /// <summary>
    /// ValidateAsync is the only validation the builder UI runs before a save, and it
    /// checks a transient version projected from the edit model. If that projection
    /// drops attachments then every attachment rule in the validator is dead code on
    /// the path an author actually travels.
    /// </summary>
    [TestMethod]
    public async Task ValidateAsync_rejects_an_automatic_version_wide_attachment()
    {
        var model = await _client.GetWorkflowAsync(await SeededVersionIdAsync());

        model!.SubWorkflows.Add(new SubWorkflowAttachmentEditModel
        {
            SubWorkflowDefinitionId = await SubWorkflowDefinitionIdAsync(),
            TaskLocalId = null,
            IsAutomatic = true
        });

        var errors = await _client.ValidateAsync(model);

        Assert.IsTrue(errors.Any(e => e.Code == "attachment.automatic-needs-task"),
            "The validator's attachment rules are unreachable from the builder seam. " +
            $"Got: {string.Join("; ", errors.Select(e => e.Code))}");
    }

    // ──────────────────────────── The outcome catalogue ────────────────────────────

    [TestMethod]
    public async Task An_outcome_added_to_the_catalogue_is_offered_and_canonicalised()
    {
        // Canonicalising is the whole reason a catalogue beats a text field. This key is
        // compared as a string in two places that did not agree about case -- a route
        // resolved in SQL under the database's collation, and a host's route condition in C#
        // ordinally -- so one spelling per outcome is the point.
        var created = await _client.CreateOutcomeTypeAsync("  Needs Review  ", "Needs Review");

        Assert.AreEqual("needs review", created.Key, "The key was not trimmed and lowered.");

        var catalogue = await _client.GetOutcomeTypesAsync();

        CollectionAssert.Contains(catalogue.Select(o => o.Key).ToList(), "needs review");
    }

    [TestMethod]
    public async Task Adding_the_same_outcome_twice_returns_the_one_that_is_there()
    {
        var first = await _client.CreateOutcomeTypeAsync("acknowledged", "Acknowledged");
        var second = await _client.CreateOutcomeTypeAsync("ACKNOWLEDGED", "Something else");

        Assert.AreEqual(first.Id, second.Id, "A second row under a differently-cased key is "
            + "exactly the duplicate this is meant to prevent.");
        Assert.AreEqual("Acknowledged", second.DisplayName,
            "The existing row wins; the second call does not quietly rename it.");
    }

    [TestMethod]
    public async Task A_retired_outcome_stops_being_offered()
    {
        var created = await _client.CreateOutcomeTypeAsync("superseded", "Superseded");

        await _client.ArchiveOutcomeTypeAsync(created.Id);

        var catalogue = await _client.GetOutcomeTypesAsync();

        CollectionAssert.DoesNotContain(catalogue.Select(o => o.Key).ToList(), "superseded");
    }

    [TestMethod]
    public async Task Retiring_an_outcome_leaves_the_workflows_using_it_alone()
    {
        // The reason a task copies the key rather than pointing at the catalogue row. A
        // published version is immutable; tidying a picker must not reach into it.
        var created = await _client.CreateOutcomeTypeAsync("approved", "Approved");

        var before = await _client.GetWorkflowAsync(await SeededVersionIdAsync());
        var usingIt = before!.Tasks
            .Count(t => t.Outcomes.Any(o => o.OutcomeKey == "approved"));

        Assert.IsGreaterThan(0, usingIt, "The seeded graph routes on 'approved'.");

        await _client.ArchiveOutcomeTypeAsync(created.Id);

        var after = await _client.GetWorkflowAsync(await SeededVersionIdAsync());

        Assert.AreEqual(usingIt, after!.Tasks
            .Count(t => t.Outcomes.Any(o => o.OutcomeKey == "approved")));
    }

    [TestMethod]
    public async Task A_retired_task_type_stops_being_offered()
    {
        var created = await _client.CreateTaskTypeAsync("temporary-step", "Temporary Step");

        await _client.ArchiveTaskTypeAsync(created.Id);

        var types = await _client.GetTaskTypesAsync();

        CollectionAssert.DoesNotContain(types.Select(t => t.Key).ToList(), "temporary-step");
    }

    [TestMethod]
    public async Task A_host_with_a_closed_vocabulary_narrows_the_catalogue()
    {
        // WorkflowBuilderOptions.Outcomes stops meaning "the only list there is" and starts
        // meaning "of the catalogue, the ones this host can read back". A host that
        // translates outcome keys must not have an author pick one it cannot translate.
        _ = await _client.CreateOutcomeTypeAsync("approved", "Approved");
        _ = await _client.CreateOutcomeTypeAsync("escalated", "Escalated");

        var narrowed = new EfWorkflowBuilderClient(
            _host.Db,
            new WorkflowTriggerRegistry([]),
            [],
            new WorkflowBuilderOptions { Outcomes = ["approved"] },
            new TestEditorActorAccessor());

        var catalogue = await narrowed.GetOutcomeTypesAsync();

        CollectionAssert.Contains(catalogue.Select(o => o.Key).ToList(), "approved");
        CollectionAssert.DoesNotContain(catalogue.Select(o => o.Key).ToList(), "escalated");
    }

    [TestMethod]
    public async Task The_inventory_shows_retired_entries_and_what_uses_each()
    {
        // What a management screen needs and the pickers deliberately refuse to show. A
        // picker's job is to offer what an author may choose; this one's is to say what is
        // in the database.
        var created = await _client.CreateTaskTypeAsync("retired-step", "Retired Step");
        await _client.ArchiveTaskTypeAsync(created.Id);

        var inventory = await _client.GetTypeInventoryAsync();

        var retired = inventory.TaskTypes.SingleOrDefault(t => t.Key == "retired-step");

        Assert.IsNotNull(retired, "A retired type is invisible to the picker and must not be "
            + "invisible here, or nobody can bring it back.");
        Assert.IsTrue(retired.IsArchived);
        Assert.AreEqual(0, retired.UsedBy);

        var inUse = inventory.TaskTypes.First(t => !t.IsArchived && t.UsedBy > 0);

        Assert.IsGreaterThan(0, inUse.UsedBy, "The seeded workflow uses task types.");
    }

    [TestMethod]
    public async Task Outcome_keys_in_use_but_not_catalogued_are_listed_for_adoption()
    {
        // The normal state of a database that had workflows before it had a catalogue, and
        // the reason this is surfaced rather than hidden: those keys are what the workflows
        // route on, so a catalogue that does not mention them is the thing out of date.
        var inventory = await _client.GetTypeInventoryAsync();

        CollectionAssert.Contains(
            inventory.UncataloguedOutcomeKeys.ToList(), "approved",
            "The seeded workflow routes on 'approved' and nothing has catalogued it.");

        _ = await _client.CreateOutcomeTypeAsync("approved", "Approved");

        var after = await _client.GetTypeInventoryAsync();

        CollectionAssert.DoesNotContain(after.UncataloguedOutcomeKeys.ToList(), "approved");
        Assert.IsGreaterThan(0, after.Outcomes.Single(o => o.Key == "approved").UsedBy,
            "Adopting a key in use should show what uses it.");
    }

    [TestMethod]
    public async Task A_differently_cased_key_counts_as_catalogued()
    {
        // Adopting "Approved" stores "approved", and the capitalised tasks must then stop
        // being offered for adoption -- otherwise the list never empties and a second entry
        // looks necessary.
        _ = await _client.CreateOutcomeTypeAsync("APPROVED", "Approved");

        var inventory = await _client.GetTypeInventoryAsync();

        Assert.IsFalse(
            inventory.UncataloguedOutcomeKeys.Any(k =>
                string.Equals(k, "approved", StringComparison.OrdinalIgnoreCase)),
            $"Still offered: {string.Join(", ", inventory.UncataloguedOutcomeKeys)}");
    }

    [TestMethod]
    public async Task A_retired_type_can_be_brought_back()
    {
        var created = await _client.CreateOutcomeTypeAsync("paused", "Paused");

        await _client.ArchiveOutcomeTypeAsync(created.Id);
        Assert.IsFalse((await _client.GetOutcomeTypesAsync()).Any(o => o.Key == "paused"));

        await _client.RestoreOutcomeTypeAsync(created.Id);
        Assert.IsTrue((await _client.GetOutcomeTypesAsync()).Any(o => o.Key == "paused"),
            "Retiring is reversible; that is the difference between it and deleting.");
    }

    [TestMethod]
    public async Task A_route_naming_an_outcome_its_task_does_not_declare_is_reported()
    {
        // This used to be writable. A route carried the outcome key as text, so nothing
        // stopped it naming one the task never declared -- and completion validates the
        // outcome before looking for a route, so the route could never fire. It was a dead
        // edge nobody could see.
        //
        // Routes point at an outcome row now, so the database refuses it; this is the
        // message an author gets before the save is attempted.
        var model = await _client.GetWorkflowAsync(await SeededVersionIdAsync());

        var task = model!.Tasks.First(t => t.Outcomes.Count > 0);

        task.Routes.Add(new RouteEditModel
        {
            OutcomeKey = "an-outcome-this-task-does-not-declare",
            NextTaskLocalId = model.Tasks.First(t => t.LocalId != task.LocalId).LocalId,
            IsDefault = true,
            Order = 99
        });

        var errors = await _client.ValidateAsync(model);

        Assert.IsTrue(errors.Any(e => e.Code == "ROUTE_NO_OUTCOME"),
            $"Got: {string.Join("; ", errors.Select(e => e.Code))}");
    }

    [TestMethod]
    public async Task A_route_whose_outcome_is_not_declared_is_not_saved()
    {
        // The save path's half of the same rule. Skipping the route is what keeps the
        // foreign key satisfiable; the validator above is what tells the author why their
        // edge disappeared.
        var versionId = await _client.CreateDraftVersionAsync(await SeededVersionIdAsync());
        var model = await _client.GetWorkflowAsync(versionId);

        var task = model!.Tasks.First(t => t.Outcomes.Count > 0);
        var routesBefore = task.Routes.Count;

        task.Routes.Add(new RouteEditModel
        {
            OutcomeKey = "an-outcome-this-task-does-not-declare",
            NextTaskLocalId = model.Tasks.First(t => t.LocalId != task.LocalId).LocalId,
            IsDefault = true,
            Order = 99
        });

        _ = await _client.SaveAsync(model);

        // By label, not by LocalId: a LocalId is minted per read for the editor's own
        // bookkeeping and does not survive a round trip.
        var reloaded = await _client.GetWorkflowAsync(versionId);
        var savedTask = reloaded!.Tasks.Single(t => t.Label == task.Label);

        Assert.HasCount(routesBefore, savedTask.Routes,
            "The undeclared route was written, which the foreign key should have made "
            + "impossible.");
    }

    [TestMethod]
    public async Task ValidateAsync_accepts_the_seeded_attachment()
    {
        var model = await _client.GetWorkflowAsync(await SeededVersionIdAsync());

        var errors = await _client.ValidateAsync(model!);

        // The seeded graph publishes cleanly, so mapping attachments into the transient
        // version must not have invented an error for a perfectly good attachment.
        Assert.IsEmpty(errors, string.Join("; ", errors.Select(e => e.Message)));
    }

    [TestMethod]
    public async Task An_attachment_pointing_at_a_deleted_task_blocks_the_save()
    {
        var versionId = await DraftWithAttachmentAsync();
        var model = await _client.GetWorkflowAsync(versionId);

        // Point it at a task, then delete that task: the same shape as a route left
        // dangling by an edit, and reported the same structural way.
        var doomed = model!.Tasks.Single(t => t.TaskTypeKey == "enter-record");
        model.SubWorkflows[0].TaskLocalId = doomed.LocalId;
        model.Tasks.Remove(doomed);

        var saved = await _client.SaveAsync(model);

        Assert.IsFalse(saved.Success, "A dangling attachment was silently dropped instead.");
        Assert.IsTrue(saved.Errors.Any(e => e.Code == "ATTACHMENT_NO_TARGET"),
            string.Join("; ", saved.Errors.Select(e => e.Code)));
    }

    [TestMethod]
    public async Task A_duplicate_attachment_fails_the_save_rather_than_the_index()
    {
        var versionId = await DraftWithAttachmentAsync();
        var model = await _client.GetWorkflowAsync(versionId);

        model!.SubWorkflows.Add(model.SubWorkflows[0].Clone());

        // Without a structural check this reaches the database and dies on the unique
        // index over (version, task, sub-workflow) as a raw DbUpdateException.
        var saved = await _client.SaveAsync(model);

        Assert.IsFalse(saved.Success);
        Assert.IsTrue(saved.Errors.Any(e => e.Code == "ATTACHMENT_DUPLICATE"),
            string.Join("; ", saved.Errors.Select(e => e.Code)));
    }

    // ──────────────────── Attachments through the save round trip ────────────────────

    /// <summary>
    /// A draft carrying one version-wide attachment.
    ///
    /// The draft comes from <see cref="IWorkflowBuilderClient.CreateDraftVersionAsync"/>
    /// on the seeded published version, the same way
    /// <c>CreateDraftVersion_copies_a_published_graph_without_touching_it</c> obtains one.
    /// The attachment is inserted directly because nothing on the client writes one yet.
    /// </summary>
    private async Task<int> DraftWithAttachmentAsync()
    {
        var publishedId = await SeededVersionIdAsync();
        var draftId = await _client.CreateDraftVersionAsync(publishedId);

        // The seeder already hangs Technical Review off Provide Input on the published
        // version, and the draft now inherits it. Drop what came across so these tests
        // count exactly one attachment of a scope they chose.
        _host.Db.WorkflowSubWorkflowAttachments.RemoveRange(
            await _host.Db.WorkflowSubWorkflowAttachments
                .Where(a => a.WorkflowDefinitionVersionId == draftId).ToListAsync());

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        _host.Db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
        {
            SubWorkflowDefinitionId = subWorkflowId,
            WorkflowDefinitionVersionId = draftId,
            TaskDefinitionId = null,
            IsBlocking = true,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        return draftId;
    }

    [TestMethod]
    public async Task An_attachment_survives_a_draft_save()
    {
        var versionId = await DraftWithAttachmentAsync();

        var model = await _client.GetWorkflowAsync(versionId);
        Assert.HasCount(1, model!.SubWorkflows, "Attachment not loaded into the model.");

        var saved = await _client.SaveAsync(model);
        Assert.IsTrue(saved.Success, string.Join("; ", saved.Errors.Select(e => e.Message)));

        var reloaded = await _client.GetWorkflowAsync(saved.VersionId);

        Assert.HasCount(1, reloaded!.SubWorkflows,
            "Saving a draft destroyed its attachments.");
        Assert.IsNull(reloaded.SubWorkflows[0].TaskLocalId, "Version-wide scope was lost.");
    }

    [TestMethod]
    public async Task An_attachment_survives_a_new_draft_version()
    {
        var versionId = await DraftWithAttachmentAsync();

        var draftId = await _client.CreateDraftVersionAsync(versionId);
        var draft = await _client.GetWorkflowAsync(draftId);

        Assert.HasCount(1, draft!.SubWorkflows,
            "A new version did not carry its attachments forward.");
    }

    [TestMethod]
    public async Task A_task_scoped_attachment_keeps_pointing_at_its_task()
    {
        var versionId = await DraftWithAttachmentAsync();
        var model = await _client.GetWorkflowAsync(versionId);

        // Local ids are minted fresh on every load, so the attachment has to be checked
        // against something stable about the task it names. "enter-record" appears
        // exactly once in the seeded graph.
        var target = model!.Tasks.Single(t => t.TaskTypeKey == "enter-record");
        model.SubWorkflows[0].TaskLocalId = target.LocalId;

        var saved = await _client.SaveAsync(model);
        var reloaded = await _client.GetWorkflowAsync(saved.VersionId);

        // Task definitions are recreated wholesale on save, so the local id resolves at
        // all only if the attachment was written against the row created in *this* save
        // rather than left pointing at the old database id.
        var reloadedLocalId = reloaded!.SubWorkflows[0].TaskLocalId;
        Assert.IsNotNull(reloadedLocalId, "Task scope was lost.");
        Assert.AreEqual("enter-record", reloaded.Find(reloadedLocalId.Value)!.TaskTypeKey);

        // Save once more. This time the row already in the database names a task
        // definition the save deletes, and that foreign key is NoAction - so the save
        // only gets through because ClearGraphAsync removes the attachment first.
        var resaved = await _client.SaveAsync(reloaded);
        Assert.IsTrue(resaved.Success, string.Join("; ", resaved.Errors.Select(e => e.Message)));

        var again = await _client.GetWorkflowAsync(resaved.VersionId);
        Assert.HasCount(1, again!.SubWorkflows);
        Assert.AreEqual("enter-record",
            again.Find(again.SubWorkflows[0].TaskLocalId!.Value)!.TaskTypeKey);
    }

    [TestMethod]
    public async Task A_reminder_lead_time_survives_a_save_and_reload()
    {
        // Attachments were neither loaded nor rewritten for a full day before anybody
        // noticed, because the builder appeared to save them. This is that test, for the
        // field added this time.
        var model = (await _client.GetWorkflowAsync(await SeededVersionIdAsync()))!;

        var typeKey = model.Tasks[0].TaskTypeKey;
        model.Tasks[0].ReminderLeadTimeMinutes = 2880;

        // The seeded version is published, so SaveAsync forks a draft rather than editing
        // in place -- reload the version it actually wrote to, as the file's other
        // round-trip tests do.
        var saved = await _client.SaveAsync(model);
        var reloaded = (await _client.GetWorkflowAsync(saved.VersionId))!;

        Assert.AreEqual(2880, reloaded.Tasks
            .First(t => t.TaskTypeKey == typeKey)
            .ReminderLeadTimeMinutes);
    }
}
