# `Workflow.AspNetCore` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a `Workflow.AspNetCore` package that maps every `IWorkflowEngine` operation to an HTTP endpoint, taking the actor from the authenticated principal rather than the request body.

**Architecture:** A minimal-API endpoint group over `IWorkflowEngine`. Two host seams — one supplying the actor id from `HttpContext`, one supplying the actor's branch keys for the inbox. A result mapper turns the engine's `Result<T>` into `ProblemDetails` with honest status codes, which requires a new `WorkflowNotFoundException` in the engine so 404 can be told from 409 without parsing messages.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, MSTest, `Microsoft.AspNetCore.TestHost`, SQL Server integration tests.

**Spec:** `docs/superpowers/specs/2026-08-24-workflow-aspnetcore-design.md`

---

## Before you start

Every `dotnet` command in this plan needs these two lines first — `dotnet` is not on the
PATH and the integration tests need the SA password:

```bash
export DOTNET_ROOT=~/.dotnet && export PATH=$DOTNET_ROOT:$PATH
source "$WORKFLOW_DEV_ENV"
```

Verify the baseline before changing anything:

```bash
cd <repo root> && dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, and `Passed! - Failed: 0, Passed: 261`.

**The 261 existing tests must still pass, untouched, at every commit in this plan.** If
one breaks, the change was wrong — do not edit the test to match.

Note on incremental builds: a plain `dotnet build` can report 0 warnings when a project
was not rebuilt. Use `--no-incremental` whenever you are checking the warning count.

## File structure

| File | Responsibility |
|---|---|
| `src/Workflow.Core/Abstractions/Authorization.cs` *(modify)* | Gains `WorkflowNotFoundException`, beside the authorization one it is modelled on |
| `src/Workflow.Persistence.EF/WorkflowEngine*.cs` *(modify)* | Eleven lookup throws become the new type |
| `src/Workflow.AspNetCore/Workflow.AspNetCore.csproj` | New project |
| `src/Workflow.AspNetCore/WorkflowEndpointOptions.cs` | The claim type to read the actor from |
| `src/Workflow.AspNetCore/IWorkflowEndpointActorAccessor.cs` | Actor seam + claims default |
| `src/Workflow.AspNetCore/IWorkflowEndpointBranchKeyResolver.cs` | Branch-key seam + empty default |
| `src/Workflow.AspNetCore/WorkflowEndpointActor.cs` | The single resolve-and-guard chokepoint |
| `src/Workflow.AspNetCore/WorkflowResultMapper.cs` | `Result<T>` → `ProblemDetails` |
| `src/Workflow.AspNetCore/WorkflowEndpointRequests.cs` | Request bodies, none carrying an actor id |
| `src/Workflow.AspNetCore/WorkflowEndpointExtensions.cs` | `AddWorkflowEndpoints` + `MapWorkflowEndpoints` |
| `src/Workflow.AspNetCore/TaskEndpoints.cs` | Task mutations |
| `src/Workflow.AspNetCore/RunEndpoints.cs` | Run + sub-workflow + fork mutations |
| `src/Workflow.AspNetCore/ReadEndpoints.cs` | The GET read side |
| `src/Workflow.AspNetCore/InboxEndpoints.cs` | `GET /inbox` |
| `tests/Workflow.Tests/EndpointTestHost.cs` | In-process web host over a real database |
| `tests/Workflow.Tests/NotFoundExceptionTests.cs` | The engine exception change |
| `tests/Workflow.Tests/EndpointActorTests.cs` | 401 behaviour and the reserved-id guard |
| `tests/Workflow.Tests/EndpointErrorMappingTests.cs` | 403 / 404 / 409 / 500 |
| `tests/Workflow.Tests/EndpointRouteTests.cs` | A happy path per route |
| `tests/Workflow.Tests/EndpointInboxTests.cs` | The branch-key seam |

Endpoints are split by subject rather than crammed into one file: the route table is
where copy-paste slips hide, and four focused files stay readable.

---

## Task 1: `WorkflowNotFoundException`

The engine throws `InvalidOperationException` for everything, so a missing task and an
already-completed task are indistinguishable to a caller. This adds a type for the first
case only.

**Files:**
- Modify: `src/Workflow.Core/Abstractions/Authorization.cs` (append)
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.cs:122-123, 472`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Reads.cs:56, 139, 181`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Fork.cs:52-53, 173`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Queries.cs:120-121, 163`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs:113-114, 449`
- Test: `tests/Workflow.Tests/NotFoundExceptionTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Workflow.Tests/NotFoundExceptionTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;

namespace Workflow.Tests;

/// <summary>
/// Every "no row with this id" refusal is its own exception type, so a host can map it
/// to 404 without reading the message. The messages themselves are unchanged, which is
/// why no existing test needed touching.
/// </summary>
[TestClass]
public class NotFoundExceptionTests
{
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();
        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [TestMethod]
    public async Task It_still_derives_from_InvalidOperationException()
    {
        // The whole point of deriving: a host catching the old type keeps working.
        var ex = new WorkflowNotFoundException("Task", "99");

        Assert.IsInstanceOfType<InvalidOperationException>(ex);
        Assert.AreEqual("Task 99 not found.", ex.Message);
        Assert.AreEqual("Task", ex.EntityKind);
        Assert.AreEqual("99", ex.Id);
    }

    [TestMethod]
    public async Task It_reports_a_missing_task_as_not_found()
    {
        var result = await _host.Engine.CompleteTaskAsync(999_999, "approve", "user-x");

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_a_missing_run_as_not_found()
    {
        var result = await _host.Engine.GetRunAsync(999_999);

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_a_missing_fork_manifest_as_not_found()
    {
        var result = await _host.Engine.GetForkManifestAsync(999_999);

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_a_missing_sub_workflow_instance_as_not_found()
    {
        var result = await _host.Engine.CancelSubWorkflowAsync(999_999, "user-x");

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_leaves_state_refusals_as_plain_invalid_operations()
    {
        // The definition exists; it has no published version. That is a state refusal,
        // not an absence, and must NOT convert -- this is the line the conversion draws.
        var draft = new WorkflowDefinition { Name = "Never published" };
        _host.Db.WorkflowDefinitions.Add(draft);
        await _host.Db.SaveChangesAsync();

        var result = await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), draft.Id, "user-x");

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<InvalidOperationException>(result.UnwrapError());
        Assert.IsNotInstanceOfType<WorkflowNotFoundException>(
            result.UnwrapError(),
            "an unpublished definition exists -- it is a conflict, not a 404");
    }

    [TestMethod]
    public async Task It_reports_a_started_task_normally()
    {
        // Guards the conversion from over-reaching: a real task still completes.
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "2"), _definitionId, "user-originator")).Unwrap();

        var outcomes = (await _host.Engine.GetValidOutcomesAsync(run.Tasks.Single().Id)).Unwrap();
        var result = await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, outcomes.First().OutcomeKey, "user-originator");

        Assert.IsTrue(result.IsOk, result.IsError ? result.UnwrapError().Message : "");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test --filter FullyQualifiedName~NotFoundExceptionTests
```

Expected: compile failure — `The type or namespace name 'WorkflowNotFoundException' could not be found`.

- [ ] **Step 3: Add the exception**

Append to `src/Workflow.Core/Abstractions/Authorization.cs`:

```csharp
/// <summary>
/// Thrown when an operation names an entity id that has no row, and surfaced to the
/// caller as a failed <c>Result</c> like every other engine refusal.
///
/// Derives from <see cref="InvalidOperationException"/> for the same reason
/// <see cref="WorkflowAuthorizationException"/> does: the engine's idiom is to throw one
/// inside a <c>Try.RunAsync</c> body, so deriving leaves every existing call site and
/// every existing test working untouched, while a host that wants to answer HTTP 404
/// rather than 400 can catch this type specifically.
///
/// <para><b>Absence only.</b> This means "there is no such row". An entity that exists
/// but is in a state the caller cannot use — an unpublished version, a completed task —
/// stays a plain <see cref="InvalidOperationException"/>. Drawing the line at row lookup
/// is what keeps the distinction mechanical rather than a judgement call at each site.
/// </para>
/// </summary>
/// <param name="entityKind">What was looked for, e.g. "Task", capitalised for the message.</param>
/// <param name="id">The id, stringified — a fork group is a Guid, everything else an int.</param>
public sealed class WorkflowNotFoundException(string entityKind, string id)
    : InvalidOperationException($"{entityKind} {id} not found.")
{
    public string EntityKind { get; } = entityKind;

    public string Id { get; } = id;
}
```

The message format reproduces the existing text exactly — `"Task 99 not found."` — so
nothing that reads a message changes behaviour.

- [ ] **Step 4: Convert the eleven throw sites**

Each is a literal replacement. `src/Workflow.Persistence.EF/WorkflowEngine.cs` line 122:

```csharp
                ?? throw new WorkflowNotFoundException(
                    "Workflow version", workflowDefinitionVersionId.ToString());
```

Line 472:

```csharp
        ?? throw new WorkflowNotFoundException("Task", taskId.ToString());
```

`WorkflowEngine.Reads.cs` line 56:

```csharp
                throw new WorkflowNotFoundException("Task", taskId.ToString());
```

Line 139:

```csharp
                ?? throw new WorkflowNotFoundException("Task", taskId.ToString());
```

Line 181:

```csharp
                ?? throw new WorkflowNotFoundException("Fork manifest", manifestId.ToString());
```

`WorkflowEngine.Fork.cs` line 52:

```csharp
                ?? throw new WorkflowNotFoundException(
                    "Convergence task definition", convergenceTaskDefinitionId.ToString());
```

Line 173:

```csharp
                ?? throw new WorkflowNotFoundException("Task", convergenceTaskId.ToString());
```

`WorkflowEngine.Queries.cs` line 120:

```csharp
                ?? throw new WorkflowNotFoundException(
                    "Task definition", taskDefinitionId.ToString());
```

Line 163:

```csharp
                ?? throw new WorkflowNotFoundException("Run", runId.ToString());
```

`WorkflowEngine.SubWorkflows.cs` line 113:

```csharp
                ?? throw new WorkflowNotFoundException(
                    "Sub-workflow instance", instanceId.ToString());
```

Line 449:

```csharp
                ?? throw new WorkflowNotFoundException("Task", taskId.ToString());
```

Each of these five files already has `using Workflow.Core.Abstractions;` — verify rather
than assume, and add it if a file is missing it.

- [ ] **Step 5: Confirm no site was missed**

```bash
grep -rn 'InvalidOperationException($"[A-Z].* not found' src/Workflow.Persistence.EF --include='*.cs'
```

Expected: no output. Anything printed is a site the conversion missed.

- [ ] **Step 6: Run the tests**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, and `Passed! - Failed: 0, Passed: 268` (261 existing + 7 new).

- [ ] **Step 7: Commit**

```bash
git add src/Workflow.Core/Abstractions/Authorization.cs \
        src/Workflow.Persistence.EF/ \
        tests/Workflow.Tests/NotFoundExceptionTests.cs
git commit -m "Give absence its own exception type

Every engine refusal was an InvalidOperationException, so 'Task 99 not
found' and 'Task 99 is already completed' differed only in prose. A host
wanting to answer 404 had to match on the message.

WorkflowNotFoundException covers row lookup and nothing else -- an
unpublished version still conflicts rather than vanishing. It derives
from InvalidOperationException and reproduces the old message exactly,
so no existing caller or test changes."
```

---

## Task 2: The project skeleton

**Files:**
- Create: `src/Workflow.AspNetCore/Workflow.AspNetCore.csproj`
- Create: `src/Workflow.AspNetCore/WorkflowEndpointOptions.cs`
- Create: `src/Workflow.AspNetCore/WorkflowEndpointExtensions.cs`
- Modify: `WorkflowEngine.slnx`

No test in this task: it produces no behaviour yet, and Task 3 builds the harness that
tests all of it. This is the one task in the plan that commits without a test.

- [ ] **Step 1: Create the project file**

`src/Workflow.AspNetCore/Workflow.AspNetCore.csproj` — matching the conventions in
`Workflow.Persistence.EF.csproj` (no `Directory.Build.props` exists; each project states
its own):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Workflow.Persistence.EF\Workflow.Persistence.EF.csproj" />
  </ItemGroup>

</Project>
```

`FrameworkReference` rather than package references: this is a library targeting the
ASP.NET Core shared framework, so the host supplies it.

- [ ] **Step 2: Add the options type**

`src/Workflow.AspNetCore/WorkflowEndpointOptions.cs`:

```csharp
using System.Security.Claims;

namespace Workflow.AspNetCore;

/// <summary>
/// What the endpoint layer needs to know that is not a seam.
/// </summary>
public sealed class WorkflowEndpointOptions
{
    /// <summary>
    /// The claim the default actor accessor reads the actor id from.
    ///
    /// <see cref="ClaimTypes.NameIdentifier"/> because it is what the common
    /// authentication handlers populate. A host whose tokens carry the id elsewhere
    /// changes this rather than implementing the seam.
    /// </summary>
    public string ActorClaimType { get; set; } = ClaimTypes.NameIdentifier;
}
```

- [ ] **Step 3: Add the registration and mapping entry points**

`src/Workflow.AspNetCore/WorkflowEndpointExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workflow.AspNetCore;

public static class WorkflowEndpointExtensions
{
    /// <summary>
    /// Registers the endpoint layer's seams and their defaults.
    ///
    /// Every registration is <c>TryAdd</c>, so a host that registered its own accessor
    /// or resolver first keeps it. Call this alongside <c>AddWorkflowEngine()</c>, not
    /// instead of it — this layer adds HTTP, not the engine.
    /// </summary>
    public static IServiceCollection AddWorkflowEndpoints(
        this IServiceCollection services,
        Action<WorkflowEndpointOptions>? configure = null)
    {
        var options = new WorkflowEndpointOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton<IWorkflowEndpointActorAccessor, ClaimsActorAccessor>();
        services.TryAddScoped<IWorkflowEndpointBranchKeyResolver, NoBranchKeysResolver>();
        services.TryAddSingleton<WorkflowResultMapper>();

        return services;
    }

    /// <summary>
    /// Maps every <c>IWorkflowEngine</c> operation under <paramref name="prefix"/>.
    ///
    /// Returns the group so the host applies its own policy — this package deliberately
    /// does not call <c>RequireAuthorization</c>, because it does not know the host's
    /// scheme. It is safe without one: the actor accessor fails closed, so an
    /// unauthenticated request is refused with 401 whatever the host forgot.
    /// </summary>
    public static RouteGroupBuilder MapWorkflowEndpoints(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/workflow")
    {
        var group = endpoints.MapGroup(prefix);

        group.MapTaskEndpoints();
        group.MapRunEndpoints();
        group.MapReadEndpoints();
        group.MapInboxEndpoints();

        return group;
    }
}
```

This will not compile yet — the four `Map*Endpoints` calls and three service types arrive
in Tasks 3–9. That is expected; the next step stubs them so the tree stays green.

- [ ] **Step 4: Stub the four mapping methods so the project compiles**

Create each file with an empty mapper, to be filled in by later tasks. For example
`src/Workflow.AspNetCore/TaskEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Routing;

namespace Workflow.AspNetCore;

internal static class TaskEndpoints
{
    internal static void MapTaskEndpoints(this RouteGroupBuilder group)
    {
    }
}
```

Create `RunEndpoints.cs`, `ReadEndpoints.cs` and `InboxEndpoints.cs` identically, with
`MapRunEndpoints`, `MapReadEndpoints` and `MapInboxEndpoints` on `internal static class
RunEndpoints`, `ReadEndpoints` and `InboxEndpoints` respectively.

Also comment out the three `TryAdd` lines for types that do not exist yet
(`ClaimsActorAccessor`, `NoBranchKeysResolver`, `WorkflowResultMapper`) — Task 4 restores
the first, Task 5 the third, Task 9 the second. Leave a marker:

```csharp
        // Restored in Tasks 4, 5 and 9 as each type lands.
```

- [ ] **Step 5: Add the project to the solution**

Edit `WorkflowEngine.slnx`, inside the `/src/` folder, keeping alphabetical order:

```xml
  <Folder Name="/src/">
    <Project Path="src/Workflow.AspNetCore/Workflow.AspNetCore.csproj" />
    <Project Path="src/Workflow.Core/Workflow.Core.csproj" />
    <Project Path="src/Workflow.MudBlazor/Workflow.MudBlazor.csproj" />
    <Project Path="src/Workflow.Persistence.EF/Workflow.Persistence.EF.csproj" />
  </Folder>
```

- [ ] **Step 6: Verify it builds**

```bash
dotnet build --no-incremental
```

Expected: `Workflow.AspNetCore -> .../Workflow.AspNetCore.dll` in the output, and
`0 Warning(s)`.

- [ ] **Step 7: Commit**

```bash
git add src/Workflow.AspNetCore WorkflowEngine.slnx
git commit -m "Add the Workflow.AspNetCore project

Skeleton only: options, the two entry points, and empty route groups the
next tasks fill in."
```

---

## Task 3: The test harness

Nothing after this can be tested without an in-process web host. `TestHost` builds a raw
`ServiceProvider`, so this is a sibling rather than a change to it.

**Files:**
- Modify: `tests/Workflow.Tests/Workflow.Tests.csproj`
- Create: `tests/Workflow.Tests/EndpointTestHost.cs`
- Test: `tests/Workflow.Tests/EndpointRouteTests.cs` (smoke test only in this task)

- [ ] **Step 1: Add the test-host packages**

In `tests/Workflow.Tests/Workflow.Tests.csproj`, add to the existing `PackageReference`
group and add a framework reference:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.10" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" Version="10.0.10" />
    <PackageReference Include="MSTest" Version="4.0.2" />
  </ItemGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

And add the project reference alongside the two existing ones:

```xml
    <ProjectReference Include="..\..\src\Workflow.AspNetCore\Workflow.AspNetCore.csproj" />
```

- [ ] **Step 2: Write the harness**

`tests/Workflow.Tests/EndpointTestHost.cs`:

```csharp
using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Workflow;

using Workflow.AspNetCore;
using Workflow.Persistence.EF;

namespace Workflow.Tests;

/// <summary>
/// An in-process web host with <c>MapWorkflowEndpoints</c> mounted, over a real SQL
/// Server database created per test class — the same integration stance as
/// <see cref="TestHost"/>, for the same reasons.
///
/// It deliberately does not boot the demo's <c>Program</c>. The demo's routes are
/// document-shaped and its startup drags in a seeder, a domain and a Blazor pipeline;
/// the subject here is a generic package that has to stand on its own. It does borrow
/// <see cref="DemoDbContext"/> and the seeder, because a workflow to run against has to
/// come from somewhere and the test project already references them.
///
/// Authentication is faked by a middleware reading <c>X-Test-Actor</c>, so the package
/// needs no test hook and a test can send an id no real scheme would issue — which is
/// how the reserved-id guard gets exercised.
///
/// <c>app.StartAsync()</c> is safe here: <c>AddWorkflowEngine()</c> registers no hosted
/// services of its own — the outbox drainer and the deadline sweeper are opt-in through
/// <c>AddOutboxProcessing()</c> and its deadline counterpart — so nothing starts sweeping
/// underneath a test that did not ask for it.
/// </summary>
public sealed class EndpointTestHost : IAsyncDisposable
{
    public const string ActorHeader = "X-Test-Actor";

    private readonly WebApplication _app;

    public HttpClient Client { get; }
    public DemoDbContext Db { get; }
    public IWorkflowEngine Engine { get; }

    private EndpointTestHost(WebApplication app, HttpClient client, DemoDbContext db, IWorkflowEngine engine)
    {
        _app = app;
        Client = client;
        Db = db;
        Engine = engine;
    }

    public static async Task<EndpointTestHost> CreateAsync(
        Action<IServiceCollection>? configure = null)
    {
        var name = $"WorkflowEndpointTest_{Guid.NewGuid():N}";
        var cs = $"{TestHost.BaseConnectionString};Database={name}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddDbContext<DemoDbContext>(o =>
            o.UseSqlServer(cs, sql => sql.EnableRetryOnFailure()));
        builder.Services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<DemoDbContext>());

        builder.Services.AddWorkflowEngine()
            .AddAssignmentResolver<DemoAssignmentResolver>()
            .AddActorResolver<DemoActorResolver>()
            .AddRouteCondition<RequiresReviewCondition>()
            // The seeder puts a demo.escalate trigger on Provide Input and the builder
            // rejects a workflow whose trigger keys are not registered, so a fixture
            // missing this fails on the seeded workflow rather than on anything a test did.
            .AddTrigger<DemoEscalationTrigger>();

        builder.Services.AddWorkflowEndpoints();

        // Last, so a test can override any of the above.
        configure?.Invoke(builder.Services);

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            var actor = ctx.Request.Headers[ActorHeader].ToString();
            if (!string.IsNullOrEmpty(actor))
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, actor)], authenticationType: "Test"));
            }

            await next();
        });

        app.MapWorkflowEndpoints();

        await app.StartAsync();

        var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DemoDbContext>();
        await db.Database.EnsureCreatedAsync();
        await DemoWorkflowSeeder.SeedAsync(db);

        return new EndpointTestHost(
            app,
            app.GetTestClient(),
            db,
            scope.ServiceProvider.GetRequiredService<IWorkflowEngine>());
    }

    /// <summary>A request carrying an actor, which is what every mutating route needs.</summary>
    public HttpRequestMessage Request(HttpMethod method, string url, string actorId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ActorHeader, actorId);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Db.Database.EnsureDeletedAsync();
        }
        catch (Exception)
        {
            // A leaked test database is noise, not a test failure.
        }

        Client.Dispose();
        await _app.DisposeAsync();
    }
}
```

- [ ] **Step 3: Write the smoke test**

`tests/Workflow.Tests/EndpointRouteTests.cs` — later tasks add to this class:

```csharp
using System.Net;

using Microsoft.EntityFrameworkCore;

namespace Workflow.Tests;

/// <summary>
/// One request per route. The route table is where a copy-paste slip hides — a handler
/// wired to the wrong engine method still compiles — and only a call per route finds it.
/// </summary>
[TestClass]
public class EndpointRouteTests
{
    private EndpointTestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await EndpointTestHost.CreateAsync();
        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [TestMethod]
    public async Task It_serves_the_endpoint_group()
    {
        // Nothing is mapped yet, so 404 from routing is the pass condition: it proves
        // the host boots, the database seeds, and the pipeline answers.
        var response = await _host.Client.SendAsync(
            _host.Request(HttpMethod.Get, "/workflow/nothing-here", "user-x"));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
```

- [ ] **Step 4: Run it**

```bash
dotnet test --filter FullyQualifiedName~EndpointRouteTests
```

Expected: `Passed: 1`. A failure here is the harness, not the package — check the SQL
connection first (`docker ps` should show the SQL Server dev container up).

- [ ] **Step 5: Commit**

```bash
git add tests/Workflow.Tests/
git commit -m "Add an in-process web host for endpoint tests

Over a real database, like every other test here, and deliberately not
booting the demo's Program: the subject is a generic package.

Authentication is a middleware reading X-Test-Actor, so the package needs
no test hook and a test can send an id no real scheme would issue."
```

---

## Task 4: The actor seam and the reserved-id guard

**Files:**
- Create: `src/Workflow.AspNetCore/IWorkflowEndpointActorAccessor.cs`
- Create: `src/Workflow.AspNetCore/WorkflowEndpointActor.cs`
- Modify: `src/Workflow.AspNetCore/WorkflowEndpointExtensions.cs` (restore the `TryAddSingleton`)
- Test: `tests/Workflow.Tests/EndpointActorTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Workflow.Tests/EndpointActorTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using Workflow.Core.Model;

namespace Workflow.Tests;

/// <summary>
/// Where the actor comes from, and what happens when it is missing or forged.
///
/// <see cref="It_never_reaches_the_engine_with_a_reserved_actor_id"/> is the one that
/// matters: WorkflowActors.System short-circuits the authorization gate before the
/// policy is consulted, so an actor id starting "workflow:" is a root switch. The demo
/// guards it on every route; here it is guarded once, on the only path an actor arrives
/// through.
/// </summary>
[TestClass]
public class EndpointActorTests
{
    private EndpointTestHost _host = null!;
    private int _taskId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await EndpointTestHost.CreateAsync();

        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        _taskId = run.Tasks.Single().Id;
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private HttpRequestMessage Notes(string? actorId)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{_taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "touched" })
        };

        if (actorId is not null)
        {
            request.Headers.Add(EndpointTestHost.ActorHeader, actorId);
        }

        return request;
    }

    [TestMethod]
    public async Task It_refuses_a_request_with_no_principal()
    {
        var response = await _host.Client.SendAsync(Notes(actorId: null));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task It_accepts_a_request_carrying_an_actor()
    {
        var response = await _host.Client.SendAsync(Notes("user-originator"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task It_refuses_a_reserved_actor_id()
    {
        var response = await _host.Client.SendAsync(Notes(WorkflowActors.System));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task It_refuses_a_reserved_actor_id_whatever_its_casing()
    {
        // The engine's own gate compares ordinally, but every SQL-mediated view of the
        // actor -- inbox matching, the CreatorId and ModifierId columns -- compares under
        // a case-insensitive collation. An ordinal guard here would let this through.
        var response = await _host.Client.SendAsync(Notes("WORKFLOW:SYSTEM"));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task It_refuses_any_reserved_prefix_not_just_the_system_id()
    {
        var response = await _host.Client.SendAsync(Notes("workflow:something-invented"));

        Assert.AreEqual(
            HttpStatusCode.Forbidden,
            response.StatusCode,
            "the prefix is reserved, not just the one id in use today");
    }

    [TestMethod]
    public async Task It_never_reaches_the_engine_with_a_reserved_actor_id()
    {
        var response = await _host.Client.SendAsync(Notes(WorkflowActors.System));

        // Asserted as well as the write, so this cannot pass for the wrong reason -- a
        // 404 from an unmapped route also leaves Notes null.
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == _taskId);

        Assert.IsNull(task.Notes, "the guard must refuse before the engine writes anything");
    }

    [TestMethod]
    public async Task It_ignores_an_actor_id_in_the_body()
    {
        // The contract has no ActorId. A body that carries one anyway must change
        // nothing -- the property is not bound, so this is really asserting the shape
        // of the request record.
        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{_taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "from body", ActorId = WorkflowActors.System })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, "user-originator");

        var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == _taskId);
        Assert.AreEqual("user-originator", task.ModifierId, "the body must not choose the actor");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test --filter FullyQualifiedName~EndpointActorTests
```

Expected: all six fail with 404 — the notes route does not exist yet. Task 6 maps it;
this task builds what it depends on. (If you would rather see a clean red-to-green
inside one task, run this filter again at the end of Task 6.)

- [ ] **Step 3: Write the seam**

`src/Workflow.AspNetCore/IWorkflowEndpointActorAccessor.cs`:

```csharp
using Microsoft.AspNetCore.Http;

namespace Workflow.AspNetCore;

/// <summary>
/// Who is making this request.
///
/// The engine takes an <c>actorId</c> on every mutating method and the authorization
/// policy decides with it, so this is the single most security-relevant thing the
/// endpoint layer does. It reads an identity; it never establishes one — authentication
/// stays the host's, and a host that maps these endpoints without a scheme gets 401 on
/// every mutation, which is the right answer rather than a gap.
/// </summary>
public interface IWorkflowEndpointActorAccessor
{
    /// <summary>
    /// The actor id, or null when the request carries none — which endpoints answer 401.
    /// </summary>
    string? GetActorId(HttpContext context);
}

/// <summary>
/// The default: a claim off the authenticated principal.
///
/// Never the body, the query string or a header. A caller may assert what to do and
/// never who is doing it, and because the request records have no <c>ActorId</c> at all,
/// that is structural rather than a rule each handler has to remember.
/// </summary>
internal sealed class ClaimsActorAccessor(WorkflowEndpointOptions options)
    : IWorkflowEndpointActorAccessor
{
    // Only authenticated identities are consulted, and the claim is read from the same
    // set that is gated. A ClaimsPrincipal can carry several identities -- enrichment
    // middleware and multi-scheme setups both produce them -- and `context.User.Identity`
    // is only the *primary* one while `context.User.FindFirst` searches all of them. Gating
    // on one and reading from the other returns claims no authenticated identity asserted.
    public string? GetActorId(HttpContext context) =>
        context.User.Identities
            .Where(identity => identity.IsAuthenticated)
            .Select(identity => identity.FindFirst(options.ActorClaimType)?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
```

- [ ] **Step 4: Write the chokepoint**

`src/Workflow.AspNetCore/WorkflowEndpointActor.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Workflow.AspNetCore;

/// <summary>
/// The one place an actor id enters the endpoint layer.
///
/// Every handler that resolves an actor calls <see cref="Resolve"/> and none touches
/// the accessor directly, so the reserved-prefix check below cannot be forgotten by a
/// route added later. Reads are included wherever they resolve an actor — the inbox
/// does, and the engine consults its authorization gate on read paths too. That is the whole reason this exists rather than the check living in
/// <see cref="ClaimsActorAccessor"/>: a host may replace the accessor, and the guard
/// must outlive the replacement.
/// </summary>
internal static class WorkflowEndpointActor
{
    /// <summary>
    /// Ids the engine reserves for itself. <c>WorkflowActors.System</c> short-circuits
    /// the authorization gate <i>before</i> the policy is consulted — which is what lets
    /// the deadline sweeper act without every host policy having to allow it, and what
    /// makes the id a root switch if it ever arrives from outside.
    ///
    /// The whole prefix is refused rather than the one id in use today, so a future
    /// reserved id is covered the day it is introduced rather than the day someone
    /// remembers this file.
    /// </summary>
    private const string ReservedPrefix = "workflow:";

    /// <summary>
    /// Resolves the actor, or returns the response that refuses the request.
    ///
    /// Null means <paramref name="actorId"/> is usable. Returning the failure rather
    /// than a bool keeps the null-state analysable at the call site — an
    /// <c>out IResult?</c> paired with a bool draws CS8603 at every one of the thirteen
    /// handlers — and it matches the idiom the demo host already uses for its own guard.
    /// </summary>
    internal static IResult? Resolve(
        HttpContext http,
        IWorkflowEndpointActorAccessor accessor,
        out string actorId)
    {
        actorId = string.Empty;

        var candidate = accessor.GetActorId(http)?.Trim();

        if (string.IsNullOrEmpty(candidate))
        {
            // 401, not 403: nobody has said who this is yet.
            return Results.Problem(
                detail: "The request carries no workflow actor.",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Not authenticated");
        }

        if (candidate.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Truncated: the value comes from a claim and is unbounded, and a CRLF in it
            // would forge lines in a console or file sink.
            http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(WorkflowEndpointActor))
                .LogWarning(
                    "Refused a request whose actor id '{ActorId}' uses the reserved '{Prefix}' prefix.",
                    candidate[..Math.Min(candidate.Length, 64)],
                    ReservedPrefix);

            // 403, not 401: the caller *is* authenticated, they simply may not be this
            // actor. Answering 401 in a cookie-auth host also risks StatusCodePages
            // bouncing an already-logged-in user to a login page.
            return Results.Problem(
                detail: $"Actor id '{candidate}' is reserved for the engine.",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Reserved actor id");
        }

        actorId = candidate;

        return null;
    }
}
```

- [ ] **Step 5: Restore the accessor registration**

In `WorkflowEndpointExtensions.AddWorkflowEndpoints`, uncomment:

```csharp
        services.TryAddSingleton<IWorkflowEndpointActorAccessor, ClaimsActorAccessor>();
```

- [ ] **Step 6: Verify it builds**

```bash
dotnet build --no-incremental
```

Expected: `0 Warning(s)`. The `EndpointActorTests` still fail — the route lands in Task 6.

- [ ] **Step 7: Commit**

```bash
git add src/Workflow.AspNetCore tests/Workflow.Tests/EndpointActorTests.cs
git commit -m "Take the actor from the principal, never the body

The demo reads actorId out of the request body, which makes every caller
able to act as anyone; it is a sample and says so. A package cannot ship
that.

The reserved-prefix guard lives at one chokepoint every handler calls
rather than in the default accessor, because a host may replace the
accessor and the guard has to outlive the replacement."
```

---

## Task 5: Error mapping

**Files:**
- Create: `src/Workflow.AspNetCore/WorkflowResultMapper.cs`
- Modify: `src/Workflow.AspNetCore/WorkflowEndpointExtensions.cs` (restore the `TryAddSingleton`)
- Test: `tests/Workflow.Tests/EndpointErrorMappingTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Workflow.Tests/EndpointErrorMappingTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;

namespace Workflow.Tests;

/// <summary>
/// Four status codes, one per exception shape the engine produces.
///
/// The ordering assertion matters most: WorkflowAuthorizationException and
/// WorkflowNotFoundException both derive from InvalidOperationException, so a switch
/// that tested the base type first would answer 409 to everything and every one of these
/// tests but the last would fail.
/// </summary>
[TestClass]
public class EndpointErrorMappingTests
{
    /// <summary>
    /// Denies only the operation under test. Scoped deliberately: the fixture seeds its
    /// task by calling StartRunAsync in-process, and that goes through the same gate --
    /// a policy that denied everything would fail the setup before the request under
    /// test was ever sent.
    /// </summary>
    private sealed class DenyingPolicy : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation,
            WorkflowAuthorizationContext context,
            CancellationToken ct = default) =>
            Task.FromResult(operation == WorkflowOperation.UpdateTaskNotes
                ? WorkflowAuthorizationResult.Denied("the test says no")
                : WorkflowAuthorizationResult.Allowed);
    }

    private sealed class ExplodingPolicy : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation,
            WorkflowAuthorizationContext context,
            CancellationToken ct = default) =>
            operation == WorkflowOperation.UpdateTaskNotes
                ? throw new BadImageFormatException("a secret from the internals")
                : Task.FromResult(WorkflowAuthorizationResult.Allowed);
    }

    private static async Task<(EndpointTestHost Host, int TaskId)> StartAsync(
        Action<IServiceCollection>? configure = null)
    {
        var host = await EndpointTestHost.CreateAsync(configure);

        var definitionId = await host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        return (host, run.Tasks.Single().Id);
    }

    private static HttpRequestMessage Notes(int taskId, string actorId)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "touched" })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, actorId);

        return request;
    }

    [TestMethod]
    public async Task It_answers_403_when_the_policy_denies()
    {
        var (host, taskId) = await StartAsync(s =>
            s.AddScoped<IWorkflowAuthorizationPolicy, DenyingPolicy>());

        await using var _ = host;

        var response = await host.Client.SendAsync(Notes(taskId, "user-originator"));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        StringAssert.Contains(
            problem.GetProperty("detail").GetString(),
            "the test says no",
            "the policy's reason is the point of reporting a denial at all");

        Assert.AreEqual(
            nameof(WorkflowOperation.UpdateTaskNotes),
            problem.GetProperty("operation").GetString());
    }

    [TestMethod]
    public async Task It_answers_404_for_a_missing_task()
    {
        var (host, _) = await StartAsync();
        await using var _h = host;

        var response = await host.Client.SendAsync(Notes(999_999, "user-originator"));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task It_answers_409_for_a_state_refusal()
    {
        var (host, taskId) = await StartAsync();
        await using var _h = host;

        var outcomes = (await host.Engine.GetValidOutcomesAsync(taskId)).Unwrap();
        (await host.Engine.CompleteTaskAsync(
            taskId, outcomes.First().OutcomeKey, "user-originator")).Unwrap();

        // Completing it a second time is a conflict, not an absence.
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/workflow/tasks/{taskId}/complete")
        {
            Content = JsonContent.Create(new { OutcomeKey = outcomes.First().OutcomeKey })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, "user-originator");

        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task It_answers_500_without_leaking_the_message()
    {
        var (host, taskId) = await StartAsync(s =>
            s.AddScoped<IWorkflowAuthorizationPolicy, ExplodingPolicy>());

        await using var _h = host;

        var response = await host.Client.SendAsync(Notes(taskId, "user-originator"));

        // AuthorizeAsync catches a throwing policy and denies, so this is really
        // asserting the *shape* of the 500 arm via whatever surfaces. Either outcome is
        // acceptable except leaking the text.
        var body = await response.Content.ReadAsStringAsync();

        Assert.IsFalse(
            body.Contains("a secret from the internals", StringComparison.Ordinal),
            "an unexpected exception's message was never written for a caller");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test --filter FullyQualifiedName~EndpointErrorMappingTests
```

Expected: failures — the routes do not exist yet, so every assertion sees 404 from
routing rather than from the mapper. As with Task 4, these go green at the end of Task 6.

- [ ] **Step 3: Write the mapper**

`src/Workflow.AspNetCore/WorkflowResultMapper.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Workflow.Core.Abstractions;
using Workflow.Core.Results;

namespace Workflow.AspNetCore;

/// <summary>
/// Turns the engine's <c>Result&lt;T&gt;</c> into an HTTP response.
///
/// The engine reports every refusal as a failed Result carrying an exception, so this is
/// the only place status codes are decided.
/// </summary>
internal sealed class WorkflowResultMapper(ILogger<WorkflowResultMapper> logger)
{
    internal IResult Map<T>(Result<T> result) =>
        result.Match(value => Results.Ok(value), MapError);

    internal IResult MapUnit(Result<Unit> result) =>
        result.Match(_ => Results.Ok(), MapError);

    private IResult MapError(Exception error) => error switch
    {
        // Order is load-bearing. Both of the first two derive from
        // InvalidOperationException -- the third arm would swallow them if it came first.
        WorkflowAuthorizationException denied => Results.Problem(
            detail: denied.Reason,
            statusCode: StatusCodes.Status403Forbidden,
            title: "Not authorized",
            extensions: new Dictionary<string, object?>
            {
                ["operation"] = denied.Operation.ToString(),
            }),

        WorkflowNotFoundException missing => Results.Problem(
            detail: missing.Message,
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found"),

        // Caller error, not server error. Semantically invalid payloads -- duplicate
        // branch keys, a fork of one branch, a missing array -- reach the engine and are
        // refused with an ArgumentException; only *syntactically* malformed JSON is
        // caught by ASP.NET before a handler runs. Answering 500 here would report the
        // caller's mistake as our fault and withhold the sentence that explains it.
        // ArgumentNullException derives from ArgumentException, so this covers both.
        ArgumentException invalid => Results.Problem(
            detail: invalid.Message,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid request"),

        // What is left after denial, absence and bad input are peeled off is
        // overwhelmingly "the workflow is not in a state where this is allowed" --
        // already completed, cancelled, superseded by a fork.
        InvalidOperationException conflict => Results.Problem(
            detail: conflict.Message,
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict"),

        // The one place a message is withheld, deliberately: an unexpected exception is
        // the one class whose text was never written with a caller in mind.
        _ => LogAndHide(error),
    };

    private IResult LogAndHide(Exception error)
    {
        logger.LogError(error, "A workflow endpoint failed unexpectedly.");

        return Results.Problem(
            detail: "An unexpected error occurred.",
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Error");
    }
}
```

- [ ] **Step 4: Restore the mapper registration**

In `AddWorkflowEndpoints`, uncomment:

```csharp
        services.TryAddSingleton<WorkflowResultMapper>();
```

- [ ] **Step 5: Verify it builds**

```bash
dotnet build --no-incremental
```

Expected: `0 Warning(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/Workflow.AspNetCore tests/Workflow.Tests/EndpointErrorMappingTests.cs
git commit -m "Map engine refusals to status codes

403 for a denial, 404 for absence, 409 for a state refusal, 500 for
anything else -- and only the 500 arm withholds its message, because it
is the one whose text was never written for a caller.

The switch order is load-bearing: both typed exceptions derive from
InvalidOperationException, so testing the base type first would answer
409 to everything."
```

---

## Task 6: Task mutation routes

**Files:**
- Create: `src/Workflow.AspNetCore/WorkflowEndpointRequests.cs`
- Modify: `src/Workflow.AspNetCore/TaskEndpoints.cs`
- Test: `tests/Workflow.Tests/EndpointRouteTests.cs` (add), plus Tasks 4 and 5 go green

- [ ] **Step 1: Write the request records**

`src/Workflow.AspNetCore/WorkflowEndpointRequests.cs`:

```csharp
using Workflow.Core.Model;

namespace Workflow.AspNetCore;

// None of these carries an ActorId. That is the visible difference from the demo's
// contracts and the reason a caller cannot choose who they are: the property does not
// exist to bind, so a handler that wanted to trust one would have to add it first.

public sealed record StartRunRequest(string SubjectType, string SubjectId, int WorkflowDefinitionId);

public sealed record StartRunOnVersionRequest(
    string SubjectType, string SubjectId, int WorkflowDefinitionVersionId, bool IsTest = false);

public sealed record CompleteTaskRequest(string OutcomeKey, string? Notes = null);

public sealed record CancelTaskRequest(string? Note = null);

/// <param name="ActorId">
/// The <i>target</i> assignee — who the task is being handed to. Not the caller, who
/// comes from the principal. The demo's equivalent record has the same trap and names
/// the caller <c>ModifierId</c> to survive it.
/// </param>
public sealed record ReassignTaskRequest(string? ActorId, string? BranchKey, string? Note = null)
{
    internal WorkflowAssignment ToAssignment() => new(ActorId, BranchKey);
}

public sealed record UpdateNotesRequest(string? Notes);

public sealed record ForkTaskRequest(
    IReadOnlyList<string> BranchKeys, int ConvergenceTaskDefinitionId, string? Notes = null);

public sealed record CompleteSelectiveRequest(
    string OutcomeKey, IReadOnlyList<string> RejectedBranchKeys, string? Notes = null);

public sealed record AddAdHocTaskRequest(
    int TaskDefinitionId, string? AssignedActorId = null, string? AssignedBranchKey = null,
    string? Notes = null)
{
    internal WorkflowAssignment? ToAssignment() =>
        AssignedActorId is null && AssignedBranchKey is null
            ? null
            : new WorkflowAssignment(AssignedActorId, AssignedBranchKey);
}

public sealed record StartSubWorkflowRequest(
    int SubWorkflowDefinitionId, string? AssignedActorId = null, string? AssignedBranchKey = null,
    string? Notes = null)
{
    internal WorkflowAssignment? ToAssignment() =>
        AssignedActorId is null && AssignedBranchKey is null
            ? null
            : new WorkflowAssignment(AssignedActorId, AssignedBranchKey);
}

public sealed record CancelSubWorkflowRequest(string? Reason = null);

public sealed record AddBranchRequest(IReadOnlyList<string> BranchKeys, string? Notes = null);
```

`ReassignTaskRequest.ActorId` is the one place an `ActorId` appears, and it is the target
rather than the caller — the XML comment exists so nobody "fixes" it later.

- [ ] **Step 2: Write the task routes**

Replace `src/Workflow.AspNetCore/TaskEndpoints.cs` entirely:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Workflow.Persistence.EF;

namespace Workflow.AspNetCore;

/// <summary>
/// Mutations against a single task.
///
/// Every handler follows the same three lines: resolve the actor through
/// <see cref="WorkflowEndpointActor"/>, call the engine, map the Result. The repetition
/// is deliberate — a helper that hid the actor resolution would hide the one step that
/// must never be skipped.
/// </summary>
internal static class TaskEndpoints
{
    internal static void MapTaskEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/tasks/{taskId:int}/complete", async (
            int taskId,
            CompleteTaskRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.MapUnit(await engine.CompleteTaskAsync(
                taskId, body.OutcomeKey, actorId, body.Notes, ct));
        });

        group.MapPost("/tasks/{taskId:int}/complete-selective", async (
            int taskId,
            CompleteSelectiveRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.MapUnit(await engine.CompleteWithSelectiveRejectionAsync(
                taskId, body.OutcomeKey, body.RejectedBranchKeys, actorId, body.Notes, ct));
        });

        group.MapPost("/tasks/{taskId:int}/cancel", async (
            int taskId,
            CancelTaskRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.MapUnit(await engine.CancelTaskAsync(taskId, actorId, body.Note, ct));
        });

        group.MapPost("/tasks/{taskId:int}/reassign", async (
            int taskId,
            ReassignTaskRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            // body.ActorId is the target; actorId is the caller. Not the same person.
            return mapper.MapUnit(await engine.ReassignTaskAsync(
                taskId, body.ToAssignment(), actorId, body.Note, ct));
        });

        group.MapPut("/tasks/{taskId:int}/notes", async (
            int taskId,
            UpdateNotesRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.MapUnit(await engine.UpdateTaskNotesAsync(taskId, body.Notes, actorId, ct));
        });

        group.MapPost("/tasks/{taskId:int}/fork", async (
            int taskId,
            ForkTaskRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.Map(await engine.ForkTaskAsync(
                taskId, body.BranchKeys, body.ConvergenceTaskDefinitionId, actorId, body.Notes, ct));
        });

        group.MapPost("/tasks/{taskId:int}/adhoc", async (
            int taskId,
            AddAdHocTaskRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.Map(await engine.AddAdHocTaskAsync(
                taskId, body.TaskDefinitionId, actorId, body.ToAssignment(), body.Notes, ct));
        });

        group.MapPost("/tasks/{taskId:int}/sub-workflows", async (
            int taskId,
            StartSubWorkflowRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.Map(await engine.StartSubWorkflowAsync(
                taskId, body.SubWorkflowDefinitionId, actorId, body.ToAssignment(), body.Notes, ct));
        });
    }
}
```

- [ ] **Step 3: Add route tests**

Add to `EndpointRouteTests`, replacing `It_serves_the_endpoint_group` (routing now
answers, so that smoke test has done its job):

```csharp
    private async Task<int> StartedTaskAsync(string subjectId = "1")
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", subjectId), _definitionId, "user-originator")).Unwrap();

        return run.Tasks.Single().Id;
    }

    private async Task<string> ValidOutcomeAsync(int taskId) =>
        (await _host.Engine.GetValidOutcomesAsync(taskId)).Unwrap().First().OutcomeKey;

    private async Task<HttpResponseMessage> PostAsync(string url, object body, string actor = "user-originator")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, actor);

        return await _host.Client.SendAsync(request);
    }

    [TestMethod]
    public async Task It_completes_a_task()
    {
        var taskId = await StartedTaskAsync();

        var response = await PostAsync(
            $"/workflow/tasks/{taskId}/complete",
            new { OutcomeKey = await ValidOutcomeAsync(taskId), Notes = "done" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        Assert.AreEqual(WorkflowTaskStatus.Completed, task.Status,
            "the route must actually have moved the engine, not just answered 200");
    }

    [TestMethod]
    public async Task It_cancels_a_task()
    {
        var taskId = await StartedTaskAsync("2");

        var response = await PostAsync($"/workflow/tasks/{taskId}/cancel", new { Note = "not needed" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        Assert.AreEqual(WorkflowTaskStatus.Cancelled, task.Status);
    }

    [TestMethod]
    public async Task It_reassigns_a_task_to_the_target_not_the_caller()
    {
        var taskId = await StartedTaskAsync("3");

        var response = await PostAsync(
            $"/workflow/tasks/{taskId}/reassign",
            new { ActorId = "user-target", BranchKey = (string?)null, Note = "yours" },
            actor: "user-manager");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        Assert.AreEqual("user-target", task.AssignedToActorId, "the body names the target");
        Assert.AreEqual("user-manager", task.ModifierId, "the principal names the caller");
    }

    [TestMethod]
    public async Task It_updates_notes()
    {
        var taskId = await StartedTaskAsync("4");

        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "a note" })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, "user-originator");

        var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        Assert.AreEqual("a note", task.Notes);
    }
```

Add these usings to the top of the file:

```csharp
using System.Net.Http.Json;

using Workflow.Core.Model;
```

- [ ] **Step 4: Run the three endpoint test classes**

```bash
dotnet test --filter "FullyQualifiedName~Endpoint"
```

Expected: `EndpointActorTests` (6), `EndpointErrorMappingTests` (4) and the new
`EndpointRouteTests` all pass. This is the point where Tasks 4 and 5 go green.

- [ ] **Step 5: Commit**

```bash
git add src/Workflow.AspNetCore tests/Workflow.Tests/
git commit -m "Map the task mutation routes

Each handler resolves the actor, calls the engine, maps the Result. The
repetition is deliberate: a helper hiding the actor resolution would hide
the one step that must never be skipped.

Reassign is the trap -- the body names the target and the principal names
the caller -- so it gets a test asserting both land in the right column."
```

---

## Task 7: Run, sub-workflow and fork routes

**Files:**
- Modify: `src/Workflow.AspNetCore/RunEndpoints.cs`
- Test: `tests/Workflow.Tests/EndpointRouteTests.cs` (add)

- [ ] **Step 1: Write the routes**

Replace `src/Workflow.AspNetCore/RunEndpoints.cs` entirely:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Workflow.Core.Model;
using Workflow.Persistence.EF;

namespace Workflow.AspNetCore;

/// <summary>
/// Starting runs, and the operations that address something other than a single task —
/// a fork group, a sub-workflow instance.
/// </summary>
internal static class RunEndpoints
{
    internal static void MapRunEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/runs", async (
            StartRunRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.Map(await engine.StartRunAsync(
                new WorkflowSubject(body.SubjectType, body.SubjectId),
                body.WorkflowDefinitionId,
                actorId,
                ct));
        });

        group.MapPost("/runs/version", async (
            StartRunOnVersionRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.Map(await engine.StartRunOnVersionAsync(
                new WorkflowSubject(body.SubjectType, body.SubjectId),
                body.WorkflowDefinitionVersionId,
                actorId,
                body.IsTest,
                ct));
        });

        group.MapPost("/forks/{forkGroupId:guid}/branches", async (
            Guid forkGroupId,
            AddBranchRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.Map(await engine.AddBranchToForkAsync(
                forkGroupId, body.BranchKeys, actorId, body.Notes, ct));
        });

        group.MapPost("/sub-workflow-instances/{instanceId:int}/cancel", async (
            int instanceId,
            CancelSubWorkflowRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.MapUnit(await engine.CancelSubWorkflowAsync(
                instanceId, actorId, body.Reason, ct));
        });
    }
}
```

- [ ] **Step 2: Add route tests**

Add to `EndpointRouteTests`:

```csharp
    [TestMethod]
    public async Task It_starts_a_run()
    {
        var response = await PostAsync(
            "/workflow/runs",
            new { SubjectType = "ChangeRequest", SubjectId = "90", WorkflowDefinitionId = _definitionId });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var started = await _host.Db.WorkflowRuns.AsNoTracking()
            .AnyAsync(r => r.Subject.SubjectType == "ChangeRequest" && r.Subject.SubjectId == "90");

        Assert.IsTrue(started, "the route must have created a run");
    }

    [TestMethod]
    public async Task It_starts_a_run_on_a_named_version()
    {
        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == _definitionId && v.IsLatest)
            .Select(v => v.Id)
            .SingleAsync();

        var response = await PostAsync(
            "/workflow/runs/version",
            new { SubjectType = "ChangeRequest", SubjectId = "91", WorkflowDefinitionVersionId = versionId });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task It_answers_404_for_a_fork_group_that_does_not_exist()
    {
        var response = await PostAsync(
            $"/workflow/forks/{Guid.NewGuid()}/branches",
            new { BranchKeys = new[] { "SEC-1" } });

        Assert.AreEqual(
            HttpStatusCode.NotFound,
            response.StatusCode,
            "the :guid constraint matched, so this reached the engine and came back absent");
    }

    [TestMethod]
    public async Task It_answers_404_for_a_sub_workflow_instance_that_does_not_exist()
    {
        var response = await PostAsync(
            "/workflow/sub-workflow-instances/999999/cancel", new { Reason = "no" });

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
```

Note on the fork-group test: 404 is correct here, and confirmed. `AddBranchToForkAsync`
originally reported an unknown group as `"No fork manifest for group {id}."` — absence in
different prose, which is why the survey behind Task 1 missed it. Task 1 converted it
(`WorkflowEngine.Reads.cs:243`) along with its twin at `WorkflowEngine.cs:341`, and
`NotFoundExceptionTests.It_reports_an_unknown_fork_group_as_not_found` pins it at the
engine level.

- [ ] **Step 3: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~EndpointRouteTests"
```

Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/Workflow.AspNetCore tests/Workflow.Tests/EndpointRouteTests.cs
git commit -m "Map run, fork and sub-workflow routes"
```

---

## Task 8: The read side

**Files:**
- Modify: `src/Workflow.AspNetCore/ReadEndpoints.cs`
- Test: `tests/Workflow.Tests/EndpointRouteTests.cs` (add)

- [ ] **Step 1: Write the routes**

Replace `src/Workflow.AspNetCore/ReadEndpoints.cs` entirely:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

using Workflow.Core.Model;
using Workflow.Persistence.EF;

namespace Workflow.AspNetCore;

/// <summary>
/// The read side.
///
/// No actor resolution: reads are ungated in the engine, deliberately, and this layer
/// does not invent a second policy the in-process path would not share. See the
/// authorization spec for why, and the design spec for where the pressure will show up
/// if a host ever needs run-level visibility rules.
///
/// Responses are the engine's snapshot types serialized directly. They already are the
/// read model — a parallel set of DTOs would be two things to keep in step for no gain.
/// </summary>
internal static class ReadEndpoints
{
    internal static void MapReadEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/runs/{runId:int}", async (
            int runId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetRunAsync(runId, ct)));

        // WorkflowSubject is a class, and minimal APIs will not bind a complex type from
        // a query string -- so the two halves arrive separately and the subject is built
        // here. Deliberate, not an oversight.
        group.MapGet("/runs", async (
            string subjectType,
            string subjectId,
            IWorkflowEngine engine,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
            mapper.Map(await engine.GetRunsForSubjectAsync(
                new WorkflowSubject(subjectType, subjectId), ct)));

        group.MapGet("/runs/{runId:int}/sub-workflow-instances", async (
            int runId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetSubWorkflowInstancesAsync(runId, ct)));

        group.MapGet("/runs/{runId:int}/fork-manifests", async (
            int runId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetForkManifestsForRunAsync(runId, ct)));

        group.MapGet("/tasks/{taskId:int}/outcomes", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetValidOutcomesAsync(taskId, ct)));

        group.MapGet("/tasks/{taskId:int}/logs", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetTaskLogsAsync(taskId, ct)));

        group.MapGet("/tasks/{taskId:int}/children", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetChildTasksAsync(taskId, ct)));

        group.MapGet("/tasks/{taskId:int}/fork-context", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetForkContextAsync(taskId, ct)));

        group.MapGet("/tasks/{taskId:int}/sub-workflow-options", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetSubWorkflowOptionsAsync(taskId, ct)));

        group.MapGet("/fork-manifests/{manifestId:int}", async (
            int manifestId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetForkManifestAsync(manifestId, ct)));
    }
}
```

- [ ] **Step 2: Add route tests**

Add to `EndpointRouteTests`:

```csharp
    [TestMethod]
    public async Task It_reads_a_run()
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "80"), _definitionId, "user-originator")).Unwrap();

        var response = await _host.Client.GetAsync($"/workflow/runs/{run.Id}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "\"subjectId\":\"80\"");
    }

    [TestMethod]
    public async Task It_reads_runs_for_a_subject_from_the_query_string()
    {
        (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "81"), _definitionId, "user-originator")).Unwrap();

        var response = await _host.Client.GetAsync("/workflow/runs?subjectType=ChangeRequest&subjectId=81");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "\"subjectId\":\"81\"");
    }

    [TestMethod]
    public async Task It_reads_the_valid_outcomes_for_a_task()
    {
        var taskId = await StartedTaskAsync("82");

        var response = await _host.Client.GetAsync($"/workflow/tasks/{taskId}/outcomes");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue((await response.Content.ReadAsStringAsync()).Length > 2, "outcomes were expected");
    }

    [TestMethod]
    public async Task It_reads_task_logs_children_and_fork_context()
    {
        var taskId = await StartedTaskAsync("83");

        foreach (var suffix in new[] { "logs", "children", "fork-context", "sub-workflow-options" })
        {
            var response = await _host.Client.GetAsync($"/workflow/tasks/{taskId}/{suffix}");

            Assert.AreEqual(
                HttpStatusCode.OK, response.StatusCode, $"GET tasks/{{id}}/{suffix} failed");
        }
    }

    [TestMethod]
    public async Task It_reads_a_run_s_fork_manifests_and_sub_workflow_instances()
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "84"), _definitionId, "user-originator")).Unwrap();

        foreach (var suffix in new[] { "fork-manifests", "sub-workflow-instances" })
        {
            var response = await _host.Client.GetAsync($"/workflow/runs/{run.Id}/{suffix}");

            Assert.AreEqual(
                HttpStatusCode.OK, response.StatusCode, $"GET runs/{{id}}/{suffix} failed");
        }
    }

    [TestMethod]
    public async Task It_answers_404_reading_a_run_that_does_not_exist()
    {
        var response = await _host.Client.GetAsync("/workflow/runs/999999");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task It_does_not_require_an_actor_to_read()
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "85"), _definitionId, "user-originator")).Unwrap();

        // No X-Test-Actor header at all. Reads are ungated on purpose; if that ever
        // changes, this test is the one that should fail and force the conversation.
        var response = await _host.Client.GetAsync($"/workflow/runs/{run.Id}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
```

The JSON assertions use camelCase because that is `System.Text.Json`'s web default. If a
run of these fails on casing, check the host's `JsonSerializerOptions` rather than
changing the assertion.

- [ ] **Step 3: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~EndpointRouteTests"
```

Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/Workflow.AspNetCore tests/Workflow.Tests/EndpointRouteTests.cs
git commit -m "Map the read side

No actor resolution: reads are ungated in the engine, deliberately, and
this layer does not invent a second policy the in-process path would not
share. A test pins that, so a change of mind has to be explicit.

GET /runs takes subjectType and subjectId separately -- WorkflowSubject
is a class, and minimal APIs will not bind one from a query string."
```

---

## Task 9: The inbox and its branch-key seam

**Files:**
- Create: `src/Workflow.AspNetCore/IWorkflowEndpointBranchKeyResolver.cs`
- Modify: `src/Workflow.AspNetCore/InboxEndpoints.cs`
- Modify: `src/Workflow.AspNetCore/WorkflowEndpointExtensions.cs` (restore the `TryAddScoped`)
- Test: `tests/Workflow.Tests/EndpointInboxTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Workflow.Tests/EndpointInboxTests.cs`:

```csharp
using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.AspNetCore.Http;

using Workflow.AspNetCore;
using Workflow.Core.Model;

namespace Workflow.Tests;

/// <summary>
/// The inbox route and the seam it needs.
///
/// The engine's GetOpenTasksForActorAsync takes branch keys alongside the actor because
/// org membership is host knowledge. The route takes neither from the caller: accepting
/// ?branchKeys= would let anyone enumerate another org unit's unclaimed work by guessing.
/// </summary>
[TestClass]
public class EndpointInboxTests
{
    private sealed class FixedBranchKeys(params string[] keys) : IWorkflowEndpointBranchKeyResolver
    {
        public Task<IReadOnlyList<string>> GetBranchKeysAsync(
            HttpContext context, string actorId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(keys);
    }

    private static async Task<(EndpointTestHost Host, int TaskId)> StartAsync(
        Action<IServiceCollection>? configure = null)
    {
        var host = await EndpointTestHost.CreateAsync(configure);

        var definitionId = await host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        return (host, run.Tasks.Single().Id);
    }

    [TestMethod]
    public async Task It_requires_an_actor()
    {
        var (host, _) = await StartAsync();
        await using var _h = host;

        var response = await host.Client.GetAsync("/workflow/inbox");

        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            response.StatusCode,
            "an inbox with no actor is a question with no subject");
    }

    [TestMethod]
    public async Task It_returns_work_assigned_directly_to_the_actor()
    {
        var (host, taskId) = await StartAsync();
        await using var _h = host;

        (await host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment("user-alice", null), "user-manager")).Unwrap();

        var response = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/workflow/inbox", "user-alice"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), taskId.ToString());
    }

    [TestMethod]
    public async Task It_returns_unclaimed_branch_work_when_a_resolver_supplies_the_key()
    {
        var (host, taskId) = await StartAsync(s =>
            s.AddScoped<IWorkflowEndpointBranchKeyResolver>(_ => new FixedBranchKeys("SEC-1")));

        await using var _h = host;

        (await host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment(null, "SEC-1"), "user-manager")).Unwrap();

        var response = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/workflow/inbox", "user-bob"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), taskId.ToString());
    }

    [TestMethod]
    public async Task It_returns_no_branch_work_without_a_resolver()
    {
        var (host, taskId) = await StartAsync();
        await using var _h = host;

        (await host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment(null, "SEC-1"), "user-manager")).Unwrap();

        var response = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/workflow/inbox", "user-bob"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsFalse(
            (await response.Content.ReadAsStringAsync()).Contains(taskId.ToString(), StringComparison.Ordinal),
            "the default resolver supplies no keys, so branch work is invisible -- "
            + "that is the documented predicate, not a bug");
    }

    [TestMethod]
    public async Task It_ignores_branch_keys_supplied_by_the_caller()
    {
        var (host, taskId) = await StartAsync();
        await using var _h = host;

        (await host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment(null, "SEC-1"), "user-manager")).Unwrap();

        var response = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/workflow/inbox?branchKeys=SEC-1", "user-bob"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsFalse(
            (await response.Content.ReadAsStringAsync()).Contains(taskId.ToString(), StringComparison.Ordinal),
            "a caller must not be able to widen their own inbox");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test --filter FullyQualifiedName~EndpointInboxTests
```

Expected: compile failure — `IWorkflowEndpointBranchKeyResolver` does not exist.

- [ ] **Step 3: Write the seam**

`src/Workflow.AspNetCore/IWorkflowEndpointBranchKeyResolver.cs`:

```csharp
using Microsoft.AspNetCore.Http;

namespace Workflow.AspNetCore;

/// <summary>
/// Which org units the current actor belongs to, as opaque branch keys.
///
/// <c>IWorkflowEngine.GetOpenTasksForActorAsync</c> takes these alongside the actor and
/// documents why: the engine has no user model and no directory, and a membership seam
/// inside it would be a third way to ask a question the host can already answer.
///
/// The endpoint asks this rather than the caller. Accepting <c>?branchKeys=</c> would let
/// anyone enumerate another org unit's unclaimed work by guessing keys — an inbox that
/// answers questions about other people's inboxes.
/// </summary>
public interface IWorkflowEndpointBranchKeyResolver
{
    Task<IReadOnlyList<string>> GetBranchKeysAsync(
        HttpContext context, string actorId, CancellationToken ct);
}

/// <summary>
/// The default: no keys.
///
/// The inbox then shows only work assigned to the actor by name, and unclaimed branch
/// work is invisible. That is the engine's documented predicate rather than a failure —
/// both halves need something to match on — but it is a puzzling first experience, so a
/// host that has an org model should implement the seam.
/// </summary>
internal sealed class NoBranchKeysResolver : IWorkflowEndpointBranchKeyResolver
{
    public Task<IReadOnlyList<string>> GetBranchKeysAsync(
        HttpContext context, string actorId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
```

- [ ] **Step 4: Write the route**

Replace `src/Workflow.AspNetCore/InboxEndpoints.cs` entirely:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Workflow.Persistence.EF;

namespace Workflow.AspNetCore;

/// <summary>
/// "My open tasks" — the query a task-driven system lives on.
///
/// Returns the engine's <c>InboxTaskSnapshot</c>, not the <c>InboxItem</c> the demo's UI
/// consumes: resolving "ChangeRequest:42" into a document number and a URL is host knowledge this
/// package does not have. A host that wants labelled rows implements
/// <c>IWorkflowInboxClient</c> over this.
/// </summary>
internal static class InboxEndpoints
{
    internal static void MapInboxEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/inbox", async (
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            IWorkflowEndpointBranchKeyResolver branchKeys,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            // The one read that resolves an actor: an inbox with no subject is not a
            // question. This is not a gate on reading -- it is what the query is about.
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            var keys = await branchKeys.GetBranchKeysAsync(http, actorId, ct);

            return mapper.Map(await engine.GetOpenTasksForActorAsync(actorId, keys, ct));
        });
    }
}
```

- [ ] **Step 5: Restore the resolver registration**

In `AddWorkflowEndpoints`, uncomment:

```csharp
        services.TryAddScoped<IWorkflowEndpointBranchKeyResolver, NoBranchKeysResolver>();
```

and delete the "Restored in Tasks 4, 5 and 9" marker comment — all three are back.

- [ ] **Step 6: Run the tests**

```bash
dotnet test --filter FullyQualifiedName~EndpointInboxTests
```

Expected: `Passed: 5`.

- [ ] **Step 7: Commit**

```bash
git add src/Workflow.AspNetCore tests/Workflow.Tests/EndpointInboxTests.cs
git commit -m "Map the inbox behind a branch-key seam

The engine needs branch keys the caller must not choose -- ?branchKeys=
would let anyone enumerate another unit's unclaimed work by guessing --
so a host seam supplies them, defaulting to none.

Returns the engine's snapshot rather than the demo's labelled InboxItem:
turning ChangeRequest:42 into a document number is host knowledge."
```

---

## Task 10: Documentation

**Files:**
- Modify: `README.md:145`
- Modify: `STATE.md` (the `Not done`, `Status` and `Suggested next step` sections)
- Modify: `docs/superpowers/specs/2026-08-24-workflow-aspnetcore-design.md` (status line)

- [ ] **Step 1: Tick the roadmap item**

In `README.md`, change:

```markdown
- [ ] `Workflow.AspNetCore` — endpoints and DI extension
```

to:

```markdown
- [x] `Workflow.AspNetCore` — endpoints and DI extension
```

- [ ] **Step 2: Add an integration note to the README**

After the existing numbered "Integrating into a host" list, add:

```markdown
### Ready-made endpoints

`Workflow.AspNetCore` maps every engine operation over HTTP:

```csharp
builder.Services.AddWorkflowEndpoints();
app.MapWorkflowEndpoints().RequireAuthorization();
```

The actor comes from the authenticated principal — `ClaimTypes.NameIdentifier` by
default — never from the request body, so a caller asserts what to do and never who is
doing it. Implement `IWorkflowEndpointBranchKeyResolver` to make `GET /workflow/inbox`
return unclaimed work in the actor's org units; without one it returns only work assigned
to them by name.

The demo does **not** use this package. Its `/documenttasks` routes are document-shaped
on purpose and mirror the controller it replaced; see `samples/DemoDocuments.Server` for
a host that maps its own.
```

- [ ] **Step 3: Update STATE.md**

In the **Status** section, update the test count from 258 to the new total (it was already
stale — the count before this plan was 261).

In **Not done**, delete the `Workflow.AspNetCore` bullet.

Replace the **Suggested next step** heading's body with the remaining candidates —
timers as a workflow primitive, and bUnit coverage for the dialogs once the builder
settles — keeping the "Still open" list beneath it intact, and adding:

```markdown
- **Reads are ungated over HTTP too.** `Workflow.AspNetCore` maps the read side with no
  actor resolution, matching the engine. `EndpointRouteTests.It_does_not_require_an_actor_to_read`
  pins it, so a change of mind has to be deliberate rather than accidental.
```

- [ ] **Step 4: Mark the spec implemented**

In `docs/superpowers/specs/2026-08-24-workflow-aspnetcore-design.md`, change:

```markdown
**Status:** Designed — not yet implemented

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.
```

to:

```markdown
**Status:** Implemented 2026-08-24 — see `docs/superpowers/plans/2026-08-24-workflow-aspnetcore.md`
```

- [ ] **Step 5: Full verification**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, and every test passing — 261 existing, 7 from
Task 1, and roughly 30 endpoint tests.

- [ ] **Step 6: Commit**

```bash
git add README.md STATE.md docs/
git commit -m "Record Workflow.AspNetCore in the docs"
```

---

## Done when

- `dotnet build --no-incremental` reports 0 warnings and 0 errors.
- `dotnet test` passes, with the original 261 tests unmodified.
- `grep -rn 'InvalidOperationException($"[A-Z].* not found' src/` returns nothing.
- No request record in `WorkflowEndpointRequests.cs` has an `ActorId` except
  `ReassignTaskRequest`, where it is the target and is documented as such.
- Every handler that resolves an actor calls `WorkflowEndpointActor.Resolve` — check with
  `grep -c 'WorkflowEndpointActor.Resolve' src/Workflow.AspNetCore/*.cs` against the count of
  `MapPost`/`MapPut` in the same files, plus one for the inbox. `ReadEndpoints.cs` must
  contain zero of both. `EndpointActorTests` pins the same property from the outside.
