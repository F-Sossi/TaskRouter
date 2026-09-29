using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// Spins up a real SQL Server database per test class.
///
/// These are integration tests on purpose. The convergence guard is a filtered unique
/// index, the transaction behaviour depends on a real provider's execution strategy,
/// and the outbox depends on both — none of which an in-memory provider exercises, and
/// all of which are exactly what went wrong in the original system.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly ServiceProvider? _provider;

    public DemoDbContext Db { get; }
    public IWorkflowEngine Engine { get; }
    public IWorkflowOutboxProcessor Outbox { get; }
    public DemoNotificationSink Notifications { get; private init; } = null!;

    /// <summary>
    /// The deadline sweep — reminders and escalations both. Null on a host built without
    /// triggers: the sweeper's whole job is to dispatch, so there is nothing to test
    /// without the trigger subsystem.
    /// </summary>
    public IWorkflowDeadlineProcessor? Deadlines { get; private init; }

    /// <summary>The container, for tests that need to resolve or scope things
    /// themselves. Null when the host was built without triggers. Per-instance, not
    /// static: these test classes run in parallel and each has its own database.</summary>
    public IServiceProvider? Provider => _provider;

    private TestHost(
        DemoDbContext db,
        IWorkflowEngine engine,
        IWorkflowOutboxProcessor outbox,
        ServiceProvider? provider)
    {
        Db = db;
        Engine = engine;
        Outbox = outbox;
        _provider = provider;
    }

    public static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("WORKFLOW_TEST_CONNECTION")
        ?? $"Server=localhost,1433;User Id=sa;Password={SaPassword};TrustServerCertificate=True;Encrypt=False";

    private static string SaPassword =>
        Environment.GetEnvironmentVariable("SA_PASSWORD")
        ?? throw new InvalidOperationException(
            "Set SA_PASSWORD (or WORKFLOW_TEST_CONNECTION) to run integration tests. See README.md.");

    /// <summary>
    /// Builds a host. With <paramref name="withTriggers"/> the full DI container is
    /// used so the trigger subsystem is live; without it the engine is constructed
    /// directly, which keeps the non-trigger tests independent of that wiring.
    /// </summary>
    public static async Task<TestHost> CreateAsync(
        bool withTriggers = false,
        Action<IServiceCollection>? configure = null,
        IWorkflowDueDateResolver? dueDates = null,
        IWorkflowAuthorizationPolicy? policy = null,
        Func<DemoDbContext, IWorkflowAuthorizationPolicy>? policyFactory = null,
        Func<DemoDbContext, IWorkflowAssignmentResolver>? assignmentResolver = null)
    {
        var name = $"WorkflowTest_{Guid.NewGuid():N}";
        var cs = $"{BaseConnectionString};Database={name}";

        if (!withTriggers)
        {
            // EnableRetryOnFailure here for the same reason the demo has it: a provider
            // configured that way refuses transactions taken outside its execution
            // strategy, and a test host without it cannot catch that mistake.
            var options = new DbContextOptionsBuilder<DemoDbContext>()
                .UseSqlServer(cs, sql => sql.EnableRetryOnFailure()).Options;
            var plainDb = new DemoDbContext(options);

            await plainDb.Database.EnsureCreatedAsync();
            await DemoWorkflowSeeder.SeedAsync(plainDb);

            var effectivePolicy = policy ?? policyFactory?.Invoke(plainDb);

            // With policy: null, this path hands the engine a null policy directly, which
            // takes the early-return branch in AuthorizeAsync — no policy call, no
            // logging. The DI path below instead falls back to AllowAllAuthorizationPolicy.
            // Both permit every operation, but only the DI path exercises the policy call,
            // the warning latch, or a capturable log — pick withTriggers: true for a test
            // that needs any of those.
            var plainEngine = new WorkflowEngine(
                plainDb,
                assignmentResolver?.Invoke(plainDb)
                    ?? new DemoAssignmentResolver(plainDb, NullLogger<DemoAssignmentResolver>.Instance),
                [new RequiresReviewCondition()],
                NullLogger<WorkflowEngine>.Instance,
                dueDateResolver: dueDates,
                authorizationPolicy: effectivePolicy);

            var plainOutbox = new OutboxProcessor(
                plainDb,
                new WorkflowTriggerRegistry([]),
                new EmptyServiceProvider(),
                NullLogger<OutboxProcessor>.Instance);

            return new TestHost(plainDb, plainEngine, plainOutbox, provider: null)
            { Notifications = new DemoNotificationSink(NullLogger<DemoNotificationSink>.Instance) };
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddDbContext<DemoDbContext>(o =>
            o.UseSqlServer(cs, sql => sql.EnableRetryOnFailure()));
        services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<DemoDbContext>());

        // Before AddTaskRouter(), so its TryAddScoped default finds this already
        // present and the allow-all default stands down. policyFactory is resolved
        // against the container's own DemoDbContext, which is the same scoped instance
        // IWorkflowDbContext resolves to above -- so a factory policy queries the data
        // the engine is about to write, same as on the plain (non-DI) path.
        if (policy is not null && policyFactory is not null)
        {
            throw new ArgumentException(
                "Pass policy or policyFactory, not both.", nameof(policyFactory));
        }

        if (policy is not null)
        {
            services.AddScoped(_ => policy);
        }
        else if (policyFactory is not null)
        {
            services.AddScoped<IWorkflowAuthorizationPolicy>(sp =>
                policyFactory(sp.GetRequiredService<DemoDbContext>()));
        }

        services.AddTaskRouter()
            .AddAssignmentResolver<DemoAssignmentResolver>()
            .AddActorResolver<DemoDirectory>()
            .AddProgressSink<DemoProgressSink>()
            .AddRouteCondition<RequiresReviewCondition>()
            // Mirrors Program.cs. The seeder puts a demo.escalate trigger on Provide
            // Input, and the builder rejects a workflow whose trigger keys are not in
            // the registry — so a fixture missing this registration fails validation on
            // the seeded workflow itself, not on anything a test did.
            .AddTrigger<DemoEscalationTrigger>();

        // Registered as a singleton instance so the test can observe what was delivered.
        // A later registration wins, so this overrides anything the builder added.
        var notifications = new DemoNotificationSink(NullLogger<DemoNotificationSink>.Instance);
        services.AddSingleton<IWorkflowNotificationSink>(notifications);

        // Before configure?.Invoke, so a test that wants something else still wins.
        // The service type is written out rather than inferred: `dueDates` is declared
        // nullable, and letting inference pick the type off it is how you end up
        // registering the concrete class instead of the interface.
        if (dueDates is not null)
        {
            services.AddScoped<IWorkflowDueDateResolver>(_ => dueDates);
        }

        // Last, so a test can add its own triggers or override anything above.
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<DemoDbContext>();

        await db.Database.EnsureCreatedAsync();
        await DemoWorkflowSeeder.SeedAsync(db);

        return new TestHost(
            db,
            provider.GetRequiredService<IWorkflowEngine>(),
            provider.GetRequiredService<IWorkflowOutboxProcessor>(),
            provider)
        {
            Notifications = notifications,
            Deadlines = provider.GetRequiredService<IWorkflowDeadlineProcessor>()
        };
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

        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
        else
        {
            await Db.DisposeAsync();
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
