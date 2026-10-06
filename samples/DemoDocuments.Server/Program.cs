using Microsoft.EntityFrameworkCore;

using MudBlazor.Services;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using DemoDocuments.Server.Components;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Inbox;
using TaskRouter.Core.Runner;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;
using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Builder;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("Demo")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Demo")
    ?? "Server=localhost,1433;Database=WorkflowDemo;User Id=sa;Password=ChangeMe;TrustServerCertificate=True;Encrypt=False";

// A factory plus a scoped context resolved from it, rather than AddDbContext alone.
//
// Blazor Server keeps one scoped DbContext for the life of a circuit, and components
// render concurrently -- a page querying its own data while the runner component loads
// a run is two operations on one context, which DbContext refuses. Pages take a
// short-lived context from the factory; the engine keeps the scoped one, because it
// must share a change tracker and a transaction with the host's own writes.
builder.Services.AddDbContextFactory<DemoDbContext>(o =>
    o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<DemoDbContext>>().CreateDbContext());

// ─────────────────────────────────────────────────────────────────────────────
// This registration block is exactly what the original system would add. Nothing else changes
// in the host: the engine reaches the host's DbContext through IWorkflowDbContext,
// so engine writes and domain writes share one change tracker and one transaction.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();          // the built-in webhook trigger needs this

// AddTaskRouterFor<T> maps the host's context onto IWorkflowDbContext and registers the
// engine in one call. The separate AddScoped<IWorkflowDbContext>(...) it replaces was the
// one registration nothing could infer, and forgetting it failed on the first engine call
// rather than here.
builder.Services.AddTaskRouterFor<DemoDbContext>()
    // The builder client itself is the library's. The demo supplies the two things it cannot
    // know: which role keys DemoAssignmentResolver understands, and who is editing.
    .AddWorkflowBuilder(o =>
    {
        o.AssignmentRoles = [DemoRoles.SectionLead, DemoRoles.DivisionHead, DemoRoles.Originator];

        // The demo's two document types, so an author picks rather than types. A host with
        // no fixed set leaves this empty and the builder offers a text field instead.
        o.SubjectTypes = ["ChangeRequest", "Drawing"];
    })
    .AddWorkflowRunner()
    .AddAssignmentResolver<DemoAssignmentResolver>()
    .AddActorResolver<DemoDirectory>()
    .AddSubjectResolver<DemoSubjectResolver>()
    .AddProgressSink<DemoProgressSink>()
    .AddNotificationSink<DemoNotificationSink>()
    .AddRouteCondition<RequiresReviewCondition>()
    // Escalation is host code by design -- the engine raises TaskOverdue and has no
    // notion of who is above whom. This walks the demo's own org chart.
    .AddTrigger<DemoEscalationTrigger>()
    // Deadlines come from the document the run is about. Without this the engine stamps
    // null dates and the whole feature is inert.
    .AddDueDateResolver<DemoDueDateResolver>()
    // Who may act. Without this every operation is permitted for every actor -- which is
    // what this demo did before, and what the engine still does for a host that registers
    // no policy.
    .AddAuthorizationPolicy<DemoAuthorizationPolicy>()
    // Without this the outbox fills up and nothing drains it: after-commit triggers
    // never run at all. A short poll here because it is a demo; the default is 10s.
    .AddOutboxProcessing(o => o.PollInterval = TimeSpan.FromSeconds(5))
    // And without this nothing ever nudges anybody and nothing ever escalates. A short
    // poll for the same reason -- the default is 5 minutes, far too slow to watch working.
    .AddDeadlineProcessing(o => o.PollInterval = TimeSpan.FromSeconds(15))
    // Last, so it sees the finished container. Reports anything missing as the application
    // starts rather than at the first request, and names the fix for each -- which is the
    // difference between a wiring mistake costing a minute and costing an afternoon.
    .ValidateWiringAtStartup();

// The host's own service, shaped like the original system's IDocumentTaskService.
builder.Services.AddScoped<IDocumentTaskService, DocumentTaskService>();

// Maps a person to their org unit. Host knowledge: the engine has no idea what a
// section is, and the one place that knows keeps the actor and branch key together.

// ─────────────────────────────────────────────────────────────────────────────
// Blazor Server, so the builder UI in TaskRouter.Blazor has somewhere to render.
// IWorkflowBuilderClient is the seam: this host implements it in-process against
// the engine, and a WebAssembly host would implement the same interface over HTTP
// without the components changing.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
// The builder client is the library's now; the demo supplies only the two things it cannot
// work out for itself. AddWorkflowBuilder() is called on the engine builder below.
builder.Services.AddScoped<IWorkflowEditorActorAccessor, DemoEditorActorAccessor>();
// The runner and inbox clients are the library's too. What the demo supplies is a directory
// -- who its people are and what its sections are -- and a subject resolver saying what a
// document is called. AddWorkflowRunner() is called on the engine builder above.

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DemoDbContext>();

    // Migrate, not EnsureCreated. The engine's tables are configured onto the host's
    // context by ConfigureTaskRouter(), so they are part of the host's schema and
    // the host owns their migrations -- there is no separate library database to
    // upgrade. the original system does exactly this against the host's DbContext.
    await db.Database.MigrateAsync();

    await DemoWorkflowSeeder.SeedAsync(db);
}

// MapStaticAssets rather than UseStaticFiles: it serves the static web assets of
// referenced component libraries -- MudBlazor's css and js, and blazor.web.js itself
// -- from the build-time manifest, in every environment. UseStaticFiles only picks
// those up in Development, so a Production run renders the markup with no styling
// and no interactivity at all.
app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

static object ToDocumentView(DocumentBase d) => new
{
    d.Id,
    d.Title,
    d.DocNumber,
    Type = d.DocumentType.ToString(),
    Status = d.Status.ToString(),
    WorkItems = d.WorkItems.Select(w => new { w.Id, w.AssignedSectionId, w.CompletionPercentage })
};

static IResult ToResult<T>(Result<T> r) =>
    r.Match(Results.Ok, ex => Results.Problem(ex.Message));

static IResult ToUnit(Result<Unit> r) =>
    r.Match(_ => Results.Ok(), ex => Results.Problem(ex.Message));

// WorkflowEngine.AuthorizeAsync short-circuits on WorkflowActors.System *before* the
// policy is consulted -- that is what lets the deadline sweeper and trigger dispatcher
// act without a host policy having to remember to allow them. It also means the id is a
// root switch: any caller who can put "workflow:system" in a request body skips
// DemoAuthorizationPolicy entirely. One guard at the boundary every mutating route goes
// through, so no route has to remember this on its own. Authentication is still out of
// scope -- this only stops a caller from *impersonating the engine*, it does not say who
// the caller otherwise may be.
static bool IsReservedActorId(string? actorId) =>
    actorId is not null && actorId.StartsWith("workflow:", StringComparison.Ordinal);

static IResult? RejectReservedActorId(string? actorId) =>
    IsReservedActorId(actorId)
        ? Results.BadRequest(
            $"Actor id '{actorId}' is reserved for the engine and may not be supplied by a caller.")
        : null;

// ═══════════════════════════════ Documents ═══════════════════════════════
//
// Under /api because the Blazor UI owns /documents now. The /documenttasks surface
// below keeps its own paths: mirroring DocumentTasksController route for route is the
// point of it.

app.MapGet("/api/documents", async (DemoDbContext db) =>
    await db.Documents.AsNoTracking()
        .Select(d => new { d.Id, d.Title, d.DocNumber, d.Status, Type = d.DocumentType })
        .ToListAsync());

app.MapGet("/api/documents/{id:int}", async (int id, DemoDbContext db) =>
    await db.Documents.Include(d => d.WorkItems).AsNoTracking()
        .SingleOrDefaultAsync(d => d.Id == id) is { } doc
        ? Results.Ok(ToDocumentView(doc))
        : Results.NotFound());

/// Creating a ChangeRequest also starts its workflow — mirrors how the original system calls
/// CreateInitialTasksAsync after persisting a document.
app.MapPost("/api/documents/change-requests", async (
    DemoDbContext db, IDocumentTaskService tasks, CreateLarRequest request) =>
{
    if (RejectReservedActorId(request.ActorId) is { } rejected)
    {
        return rejected;
    }

    var changeRequest = new ChangeRequest
    {
        Title = request.Title,
        DocNumber = request.DocNumber,
        Originator = request.Originator,
        Description = request.Description,
        DueDate = request.DueDate,
        IsReplyRequired = request.IsReplyRequired,
        Urgency = request.Urgency ?? LarUrgency.Routine,
        CreatorId = request.ActorId
    };

    db.Documents.Add(changeRequest);
    await db.SaveChangesAsync();

    // Work items, one per participating section — the things progress reporting updates.
    foreach (var code in request.SectionCodes ?? [])
    {
        var section = await db.Sections.SingleOrDefaultAsync(s => s.Code == code);
        if (section is not null)
        {
            db.WorkItems.Add(new WorkItem { DocumentId = changeRequest.Id, AssignedSectionId = section.Id });
        }
    }

    await db.SaveChangesAsync();

    var status = await tasks.CreateInitialTasksAsync(changeRequest.Id, DemoDocumentType.ChangeRequest, request.ActorId);

    // Projected rather than returned raw: WorkItem has a Document back-reference, so
    // serialising the entity walks Document -> WorkItems -> Document forever.
    return status.Match(s => Results.Ok(new { document = ToDocumentView(changeRequest), workflow = s }),
                        ex => Results.Problem(ex.Message));
});

// ══════════════════ Document tasks — mirrors DocumentTasksController ══════════════════

var tasksApi = app.MapGroup("/documenttasks");

tasksApi.MapPost("/start", async (
    IDocumentTaskService svc, StartRequest r) =>
    RejectReservedActorId(r.ActorId) ??
    ToResult(await svc.CreateInitialTasksAsync(r.DocumentId, r.DocumentType, r.ActorId)));

tasksApi.MapPost("/complete/{taskId:int}", async (
    int taskId, IDocumentTaskService svc, CompleteRequest r) =>
    RejectReservedActorId(r.ActorId) ??
    ToUnit(await svc.CompleteTaskAsync(taskId, r.OutcomeKey, r.ActorId, r.Notes)));

tasksApi.MapGet("/document/{documentId:int}", async (
    int documentId, DemoDocumentType type, IDocumentTaskService svc) =>
    ToResult(await svc.GetTasksForDocumentAsync(documentId, type)));

tasksApi.MapGet("/status/{documentId:int}", async (
    int documentId, DemoDocumentType type, IDocumentTaskService svc) =>
    ToResult(await svc.GetWorkflowStatusAsync(documentId, type)));

tasksApi.MapGet("/children/{parentId:int}", async (
    int parentId, IDocumentTaskService svc) =>
    ToResult(await svc.GetChildTasksAsync(parentId)));

tasksApi.MapGet("/logs/{taskId:int}", async (
    int taskId, IDocumentTaskService svc) =>
    ToResult(await svc.GetLogsForTaskAsync(taskId)));

tasksApi.MapGet("/validoutcomes/{taskId:int}", async (
    int taskId, IDocumentTaskService svc) =>
    ToResult(await svc.GetValidOutcomesForTaskAsync(taskId)));

tasksApi.MapPost("/reassign/{taskId:int}", async (
    int taskId, IDocumentTaskService svc, ReassignRequest r) =>
    // ModifierId is the one that reaches the engine's actorId parameter -- r.ActorId here
    // is the *target* assignee, not the caller.
    RejectReservedActorId(r.ModifierId) ??
    ToUnit(await svc.ReassignTaskAsync(taskId, r.ActorId, r.SectionCode, r.ModifierId, r.Note)));

tasksApi.MapPost("/cancel/{taskId:int}", async (
    int taskId, IDocumentTaskService svc, CancelRequest r) =>
    RejectReservedActorId(r.ActorId) ??
    ToUnit(await svc.CancelTaskAsync(taskId, r.ActorId, r.Note)));

tasksApi.MapPut("/notes/{taskId:int}", async (
    int taskId, IDocumentTaskService svc, UpdateNotesRequest r) =>
    RejectReservedActorId(r.ActorId) ??
    ToUnit(await svc.UpdateTaskNotesAsync(taskId, r.Notes, r.ActorId)));

tasksApi.MapPost("/adhoc-task", async (
    IDocumentTaskService svc, AddAdHocRequest r) =>
    RejectReservedActorId(r.ActorId) ??
    ToResult(await svc.AddAdHocTaskAsync(r.ParentTaskId, r.TaskDefinitionId, r.ActorId, r.Notes)));

tasksApi.MapGet("/{taskId:int}/adhoc-task-types", async (
    int taskId, IDocumentTaskService svc) =>
    ToResult(await svc.GetAdHocTaskTypesAsync(taskId)));

tasksApi.MapPost("/fork", async (
    IDocumentTaskService svc, ForkRequest r) =>
    RejectReservedActorId(r.ActorId) ??
    ToResult(await svc.ForkTaskAsync(
        r.TaskId, r.SectionCodes, r.ConvergenceTaskDefinitionId, r.ActorId, r.Notes)));

tasksApi.MapPost("/complete-selective", async (
    IDocumentTaskService svc, SelectiveRejectionRequest r) =>
    RejectReservedActorId(r.ActorId) ??
    ToUnit(await svc.CompleteWithSelectiveRejectionAsync(
        r.TaskId, r.OutcomeKey, r.RejectedSectionCodes, r.ActorId, r.Notes)));

tasksApi.MapPost("/fork/add-branch", async (
    IDocumentTaskService svc, AddBranchRequest r) =>
    RejectReservedActorId(r.ActorId) ??
    ToResult(await svc.AddBranchToForkAsync(r.ForkGroupId, r.SectionCodes, r.ActorId, r.Notes)));

// ── Sub-workflows ──

tasksApi.MapGet("/{taskId:int}/sub-workflows", async (
    int taskId, IWorkflowEngine engine) =>
    ToResult(await engine.GetSubWorkflowOptionsAsync(taskId)));

tasksApi.MapPost("/sub-workflow", async (
    IWorkflowEngine engine, StartSubWorkflowRequest r) =>
    RejectReservedActorId(r.ActorId) ??
    ToResult(await engine.StartSubWorkflowAsync(
        r.ParentTaskId, r.SubWorkflowDefinitionId, r.ActorId, notes: r.Notes)));

tasksApi.MapGet("/sub-workflow-instances/{runId:int}", async (
    int runId, IWorkflowEngine engine) =>
    ToResult(await engine.GetSubWorkflowInstancesAsync(runId)));

tasksApi.MapGet("/fork-context/{taskId:int}", async (
    int taskId, IDocumentTaskService svc) =>
    ToResult(await svc.GetForkContextAsync(taskId)));

tasksApi.MapGet("/fork-manifest/{manifestId:int}", async (
    int manifestId, IDocumentTaskService svc) =>
    ToResult(await svc.GetForkManifestAsync(manifestId)));

tasksApi.MapGet("/fork-manifests/{documentId:int}", async (
    int documentId, DemoDocumentType type, IDocumentTaskService svc) =>
    ToResult(await svc.GetForkManifestsForDocumentAsync(documentId, type)));

// ═══════════════════════════ Task inbox and admin ═══════════════════════════

/// "My open tasks" — the query a task-driven system lives on.
///
/// Backed by IWorkflowInboxClient rather than a LINQ query of its own. The hand-written
/// version returned run ids rather than documents and forgot to exclude test runs, which
/// is the argument for the predicate living in the engine and being used from one place.
app.MapGet("/tasks/assigned/{actorId}", async (string actorId, IWorkflowInboxClient inbox) =>
    await inbox.GetInboxAsync(actorId));

app.MapGet("/workflow/definitions", async (IWorkflowDbContext db) =>
    await db.WorkflowDefinitionVersions
        .Include(v => v.WorkflowDefinition)
        .Include(v => v.Tasks).ThenInclude(t => t.TaskType)
        .AsNoTracking()
        .Where(v => v.IsLatest)
        .Select(v => new
        {
            v.Id,
            Workflow = v.WorkflowDefinition!.Name,
            v.Version,
            v.EntryTaskDefinitionId,
            Tasks = v.Tasks.Select(t => new
            {
                t.Id,
                Type = t.TaskType!.Key,
                Name = t.DisplayName ?? t.TaskType.DisplayName,
                t.IsAdHoc,
                t.IsForkable,
                t.IsConvergencePoint,
                t.IsTerminal
            })
        })
        .ToListAsync());

await app.RunAsync();

// Request contracts
internal record CreateLarRequest(
    string Title, string DocNumber, string Originator, string ActorId,
    string? Description, DateTime? DueDate, bool IsReplyRequired,
    LarUrgency? Urgency, List<string>? SectionCodes);

internal record StartRequest(int DocumentId, DemoDocumentType DocumentType, string ActorId);
internal record CompleteRequest(string OutcomeKey, string ActorId, string? Notes);
internal record ReassignRequest(string? ActorId, string? SectionCode, string ModifierId, string? Note);
internal record CancelRequest(string ActorId, string? Note);
internal record UpdateNotesRequest(string? Notes, string ActorId);
internal record AddAdHocRequest(int ParentTaskId, int TaskDefinitionId, string ActorId, string? Notes);
internal record ForkRequest(
    int TaskId, List<string> SectionCodes, int ConvergenceTaskDefinitionId, string ActorId, string? Notes);
internal record SelectiveRejectionRequest(
    int TaskId, string OutcomeKey, List<string> RejectedSectionCodes, string ActorId, string? Notes);
internal record AddBranchRequest(Guid ForkGroupId, List<string> SectionCodes, string ActorId, string? Notes);
internal record StartSubWorkflowRequest(
    int ParentTaskId, int SubWorkflowDefinitionId, string ActorId, string? Notes);

/// <summary>Exposed so integration tests can build a host.</summary>
public partial class Program;
