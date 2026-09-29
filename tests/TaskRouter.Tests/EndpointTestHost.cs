using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Workflow;

using TaskRouter.AspNetCore;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// An in-process web host with <c>MapTaskRouterEndpoints</c> mounted, over a real SQL
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
/// <c>app.StartAsync()</c> is safe here: <c>AddTaskRouter()</c> registers no hosted
/// services of its own — the outbox drainer and the deadline sweeper are opt-in through
/// <c>AddOutboxProcessing()</c> and its deadline counterpart — so nothing starts sweeping
/// underneath a test that did not ask for it.
/// </summary>
public sealed class EndpointTestHost : IAsyncDisposable
{
    public const string ActorHeader = "X-Test-Actor";

    /// <summary>
    /// Optional: the claim type <see cref="ActorHeader"/>'s value is placed under.
    /// Absent, the fake-auth middleware uses <c>ClaimTypes.NameIdentifier</c> exactly as
    /// before -- this only exists so a test can exercise a host with a custom
    /// <c>WorkflowEndpointOptions.ActorClaimType</c> without touching the default path
    /// every other test relies on.
    /// </summary>
    public const string ActorClaimTypeHeader = "X-Test-Actor-Claim-Type";

    /// <summary>
    /// Optional: authenticates the request with no actor claim at all, for exercising
    /// "authenticated but no claim" as distinct from "not authenticated". Ignored when
    /// <see cref="ActorHeader"/> is present.
    /// </summary>
    public const string AuthenticatedNoClaimHeader = "X-Test-Authenticated-No-Claim";

    private readonly WebApplication _app;

    public HttpClient Client { get; }
    public DemoDbContext Db { get; }
    public IWorkflowEngine Engine { get; }
    public IServiceProvider Services => _app.Services;

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

        builder.Services.AddTaskRouter()
            .AddAssignmentResolver<DemoAssignmentResolver>()
            .AddActorResolver<DemoDirectory>()
            .AddRouteCondition<RequiresReviewCondition>()
            // The seeder puts a demo.escalate trigger on Provide Input and the builder
            // rejects a workflow whose trigger keys are not registered, so a fixture
            // missing this fails on the seeded workflow rather than on anything a test did.
            .AddTrigger<DemoEscalationTrigger>()
            // The builder endpoints are mapped by MapTaskRouterEndpoints whether or not a
            // host registers this, so the fixture registers it -- see
            // BuilderEndpointTests.A_host_that_never_adopted_the_builder_still_starts for
            // the other half of that arrangement.
            .AddWorkflowBuilder(o =>
            {
                o.AssignmentRoles = ["reviewer", "approver"];
                o.SubjectTypes = ["ChangeRequest", "Drawing"];
                o.Outcomes = ["approved", "rejected"];
            })
            // Same arrangement as the builder: MapTaskRouterEndpoints maps the runner routes
            // whether or not a host registers this, so the fixture registers it and
            // RunnerEndpointTests.A_host_that_never_adopted_the_runner_still_starts covers
            // the other half.
            .AddWorkflowRunner()
            .AddSubjectResolver<DemoSubjectResolver>();

        builder.Services.AddScoped<
            EntityFrameworkCore.Builder.IWorkflowEditorActorAccessor, TestEditorActorAccessor>();

        builder.Services.AddTaskRouterEndpoints();

        // Last, so a test can override any of the above.
        configure?.Invoke(builder.Services);

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            var actor = ctx.Request.Headers[ActorHeader].ToString();
            if (!string.IsNullOrEmpty(actor))
            {
                var claimType = ctx.Request.Headers[ActorClaimTypeHeader].ToString();
                if (string.IsNullOrEmpty(claimType))
                {
                    claimType = ClaimTypes.NameIdentifier;
                }

                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(claimType, actor)], authenticationType: "Test"));
            }
            else if (!string.IsNullOrEmpty(ctx.Request.Headers[AuthenticatedNoClaimHeader].ToString()))
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test"));
            }

            await next();
        });

        app.MapTaskRouterEndpoints();

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
