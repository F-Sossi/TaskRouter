using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Inbox;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;

using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.EntityFrameworkCore;

public sealed class WorkflowEngineBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>Registers a host-specific trigger implementation.</summary>
    public WorkflowEngineBuilder AddTrigger<T>() where T : class, IWorkflowTrigger
    {
        Services.AddScoped<IWorkflowTrigger, T>();
        return this;
    }

    public WorkflowEngineBuilder AddAssignmentResolver<T>() where T : class, IWorkflowAssignmentResolver
    {
        Services.AddScoped<IWorkflowAssignmentResolver, T>();
        return this;
    }

    /// <summary>
    /// Supplies deadlines. Without this the engine stamps null due dates and the whole
    /// deadline feature is inert — which is the correct default for a host that has no
    /// deadlines to give.
    /// </summary>
    public WorkflowEngineBuilder AddDueDateResolver<T>() where T : class, IWorkflowDueDateResolver
    {
        Services.AddScoped<IWorkflowDueDateResolver, T>();
        return this;
    }

    /// <summary>
    /// Supplies the authorization rules. Without this every operation is permitted, which
    /// is the library's pre-authorization behaviour and a legitimate configuration for a
    /// host that gates access somewhere else.
    /// </summary>
    public WorkflowEngineBuilder AddAuthorizationPolicy<T>()
        where T : class, IWorkflowAuthorizationPolicy
    {
        Services.AddScoped<IWorkflowAuthorizationPolicy, T>();
        return this;
    }

    /// <summary>
    /// Registers the host's directory of people and org units: display names, who may be
    /// assigned to, what org units exist, and which one an actor belongs to.
    ///
    /// <para>Consumed by the runner and inbox clients — the reassign picker, the fork
    /// picker, and the org units an inbox query covers all come from here. Without one, the
    /// default answers emptily and logs once saying so; the runner still renders, with empty
    /// pickers.</para>
    ///
    /// <para>The <b>engine</b> still does not consume it. It stores opaque actor ids and
    /// never asks for a name, so registering this changes what the UI can show and not how
    /// work is routed — <see cref="IWorkflowAssignmentResolver"/> is what decides that.</para>
    /// </summary>
    public WorkflowEngineBuilder AddActorResolver<T>() where T : class, IWorkflowActorResolver
    {
        Services.AddScoped<IWorkflowActorResolver, T>();
        return this;
    }

    /// <summary>
    /// Registers what the host's subjects are called and where they live. Without one, inbox
    /// rows show raw subject keys such as <c>"ChangeRequest:42"</c> and are not clickable.
    /// </summary>
    public WorkflowEngineBuilder AddSubjectResolver<T>() where T : class, IWorkflowSubjectResolver
    {
        Services.AddScoped<IWorkflowSubjectResolver, T>();
        return this;
    }

    public WorkflowEngineBuilder AddProgressSink<T>() where T : class, IWorkflowProgressSink
    {
        Services.AddScoped<IWorkflowProgressSink, T>();
        return this;
    }

    public WorkflowEngineBuilder AddNotificationSink<T>() where T : class, IWorkflowNotificationSink
    {
        Services.AddScoped<IWorkflowNotificationSink, T>();
        return this;
    }

    public WorkflowEngineBuilder AddRouteCondition<T>() where T : class, IRouteConditionEvaluator
    {
        Services.AddScoped<IRouteConditionEvaluator, T>();
        return this;
    }

    /// <summary>
    /// Checks the wiring as the application starts and writes what it finds to the log.
    ///
    /// <para>Problems are logged as errors and, with <paramref name="throwOnProblems"/>,
    /// stop the application — which is usually what you want, because every problem it
    /// reports is one that would otherwise surface as a confusing failure at the first
    /// request instead. Warnings are logged either way and never block startup: each is a
    /// legal default, and a check that refuses to start over a deliberate choice is a check
    /// that gets switched off.</para>
    ///
    /// <para>Call it last, after the seams are registered, so it sees the finished picture.
    /// <see cref="WorkflowWiring.Inspect(IServiceProvider)"/> is the same check if you would rather run it
    /// yourself — from a health endpoint, say.</para>
    /// </summary>
    public WorkflowEngineBuilder ValidateWiringAtStartup(bool throwOnProblems = true)
    {
        // Immediately, against the descriptors. This has to beat ASP.NET Core's own
        // validate-on-build, which in Development trips first on a missing
        // IWorkflowDbContext and reports it as eight services that could not be
        // constructed -- the actual cause repeated inside each and named nowhere. Running
        // here means the author sees one sentence and the line to add instead.
        var registration = WorkflowWiring.Inspect(Services);

        if (throwOnProblems && !registration.IsHealthy)
        {
            throw new InvalidOperationException(registration.ToString());
        }

        // And again once there is a container, for what descriptors cannot answer: whether
        // the registrations actually resolve, and whether the editor accessor can name
        // somebody rather than merely existing.
        Services.AddHostedService(provider =>
            new WorkflowWiringCheck(
                provider,
                provider.GetRequiredService<ILogger<WorkflowWiringCheck>>(),
                throwOnProblems));

        return this;
    }

    /// <summary>
    /// Runs the outbox: a hosted service that drains it on a timer, and an in-process
    /// signal so <c>Background</c> triggers are picked up as soon as their transaction
    /// commits rather than at the next poll.
    ///
    /// **Without this nothing ever runs an after-commit trigger.** The engine writes the
    /// outbox row inside its transaction and returns; something has to come back for it.
    ///
    /// Register it once per process. In a web farm every instance drains the same table,
    /// which is safe — messages are claimed with a lease before any work is done.
    /// </summary>
    public WorkflowEngineBuilder AddOutboxProcessing(Action<WorkflowOutboxOptions>? configure = null)
    {
        var options = new WorkflowOutboxOptions();
        configure?.Invoke(options);

        Services.AddSingleton(options);
        Services.TryAddSingleton<IWorkflowOutboxSignal, WorkflowOutboxSignal>();

        // Only if the host has not supplied a real one — a Hangfire or queue-backed
        // implementation should win over the in-process signal.
        Services.TryAddSingleton<IWorkflowJobQueue, SignallingJobQueue>();

        Services.AddHostedService<WorkflowOutboxHostedService>();

        return this;
    }

    /// <summary>
    /// Runs the deadline sweep: a hosted service that asks
    /// <see cref="Triggers.IWorkflowDeadlineProcessor"/> on a timer for tasks that are
    /// due soon and tasks that are already late.
    ///
    /// **Without this nothing ever nudges anybody and nothing ever escalates.** The
    /// columns are still written and the inbox still shows deadlines — visibility needs no
    /// background machinery — but neither <see cref="WorkflowEventKind.TaskDueSoon"/> nor
    /// <see cref="WorkflowEventKind.TaskOverdue"/> is ever raised.
    ///
    /// Register it once per process. In a web farm every instance sweeps the same table,
    /// which is safe: each task is claimed with a conditional update before it is
    /// dispatched, so exactly one instance wins.
    ///
    /// Reminders are delivered through the ordinary trigger runtime, and the built-in
    /// <c>workflow.notify</c> trigger defaults to after-commit dispatch — so a host that
    /// wants a reminder actually delivered needs <see cref="AddOutboxProcessing"/> as
    /// well.
    /// </summary>
    public WorkflowEngineBuilder AddDeadlineProcessing(Action<WorkflowDeadlineOptions>? configure = null)
    {
        var options = new WorkflowDeadlineOptions();
        configure?.Invoke(options);

        Services.AddSingleton(options);
        Services.AddHostedService<WorkflowDeadlineHostedService>();

        return this;
    }

    /// <summary>
    /// Registers the workflow builder's server-side client — the implementation behind
    /// <c>TaskRouter.Blazor</c>'s builder components.
    ///
    /// <para>A host that runs the components in process (Blazor Server) needs only this. A
    /// WebAssembly host maps the builder endpoints in front of it and registers the HTTP client
    /// in the browser, because a browser has no DbContext.</para>
    ///
    /// <para>Supply the assignment role keys here, and register an
    /// <see cref="Builder.IWorkflowEditorActorAccessor"/> to say who is editing. Those two are the only
    /// things the builder cannot work out for itself.</para>
    /// </summary>
    public WorkflowEngineBuilder AddWorkflowBuilder(
        Action<Builder.WorkflowBuilderOptions>? configure = null)
    {
        var options = new Builder.WorkflowBuilderOptions();
        configure?.Invoke(options);

        Services.TryAddSingleton(options);
        Services.TryAddScoped<Builder.IWorkflowEditorActorAccessor,
                              Builder.UnconfiguredEditorActorAccessor>();
        Services.AddScoped<IWorkflowBuilderClient, Builder.EfWorkflowBuilderClient>();

        return this;
    }

    /// <summary>
    /// Registers the runner and inbox clients, for a host running the engine in process.
    ///
    /// <para>Separate from <see cref="AddWorkflowBuilder"/> because they are separate
    /// surfaces: a host may well want people to run workflows without letting anyone edit
    /// them, and editing a definition changes how every future run of it behaves.</para>
    ///
    /// <para>Both clients ask the host's <see cref="IWorkflowActorResolver"/> who people are
    /// and what org units exist, and the inbox additionally asks
    /// <see cref="IWorkflowSubjectResolver"/> what its subjects are called. Neither is
    /// required — the defaults answer emptily and log once — but without a directory the
    /// reassign and fork pickers are empty, and without a subject resolver inbox rows show
    /// raw keys such as <c>"ChangeRequest:42"</c>.</para>
    /// </summary>
    public WorkflowEngineBuilder AddWorkflowRunner()
    {
        Services.AddScoped<IWorkflowRunnerClient, Runner.EfWorkflowRunnerClient>();
        Services.AddScoped<IWorkflowInboxClient, Runner.EfWorkflowInboxClient>();

        return this;
    }

    /// <summary>Replaces the default in-process job queue, for a host that runs
    /// background work somewhere else.</summary>
    public WorkflowEngineBuilder AddJobQueue<T>() where T : class, IWorkflowJobQueue
    {
        Services.AddSingleton<IWorkflowJobQueue, T>();
        return this;
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the workflow engine.
    ///
    /// <para><b>This method cannot register <see cref="IWorkflowDbContext"/> for you</b> —
    /// it has no way to know which of the host's contexts implements it. Mapping the
    /// host's own DbContext onto the seam is the one line every host must write, and
    /// omitting it is not detected here: the first engine call fails at resolution time
    /// with a generic "no service for type IWorkflowDbContext".</para>
    ///
    /// <code>
    /// services.AddDbContext&lt;MyDbContext&gt;(o => o.UseSqlServer(cs));
    /// services.AddScoped&lt;IWorkflowDbContext&gt;(sp => sp.GetRequiredService&lt;MyDbContext&gt;());
    ///
    /// services.AddTaskRouter()
    ///         .AddAssignmentResolver&lt;MyAssignmentResolver&gt;()
    ///         .AddProgressSink&lt;MyProgressSink&gt;()
    ///         .AddTrigger&lt;MyDomainTrigger&gt;();
    /// </code>
    ///
    /// <para>Everything else has a default: assignment keeps whatever it was handed, due
    /// dates resolve to null, and authorization permits everything while warning once.
    /// A host that supplies only the context above gets an engine that runs.</para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="includeBuiltInTriggers">
    /// Registers the library's domain-agnostic triggers. The webhook trigger
    /// additionally needs <c>services.AddHttpClient()</c>.
    /// </param>
    /// <summary>
    /// Registers the engine against the host's own <typeparamref name="TContext"/> — the
    /// whole of the required wiring, in one call.
    ///
    /// <para>Equivalent to mapping the context onto the seam and then calling
    /// <see cref="AddTaskRouter"/>:</para>
    /// <code>
    /// services.AddScoped&lt;IWorkflowDbContext&gt;(sp => sp.GetRequiredService&lt;TContext&gt;());
    /// services.AddTaskRouter();
    /// </code>
    ///
    /// <para>It exists because that first line is the one registration no extension method
    /// can infer — <c>AddTaskRouter()</c> has no way to know which of a host's contexts
    /// implements the interface — and leaving it out does not fail at startup. It fails on
    /// the first engine call, as a generic "no service for type IWorkflowDbContext" that
    /// names neither the context nor the fix. Naming the context as a type argument is the
    /// smallest way to make it impossible to forget.</para>
    ///
    /// <para>The context must still implement <see cref="IWorkflowDbContext"/> and call
    /// <c>ConfigureTaskRouter()</c> from <c>OnModelCreating</c>; the interface needs no
    /// members of its own, so that is a declaration and one line.</para>
    /// </summary>
    public static WorkflowEngineBuilder AddTaskRouterFor<TContext>(
        this IServiceCollection services,
        bool includeBuiltInTriggers = true)
        where TContext : DbContext, IWorkflowDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IWorkflowDbContext>(provider => provider.GetRequiredService<TContext>());

        return services.AddTaskRouter(includeBuiltInTriggers);
    }

    public static WorkflowEngineBuilder AddTaskRouter(
        this IServiceCollection services,
        bool includeBuiltInTriggers = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IWorkflowEngine, WorkflowEngine>();

        // Scoped: post-commit work belongs to one unit of work and is discarded with it.
        services.TryAddScoped<IWorkflowPostCommitActions, WorkflowPostCommitActions>();
        services.AddScoped<IWorkflowTriggerDispatcher, TriggerDispatcher>();
        services.AddScoped<IWorkflowOutboxProcessor, OutboxProcessor>();
        services.AddScoped<IWorkflowDeadlineProcessor, DeadlineProcessor>();
        services.AddScoped<IWorkflowTriggerRegistry>(sp =>
            new WorkflowTriggerRegistry(sp.GetServices<IWorkflowTrigger>()));

        // A host that supplies none of these still gets a working engine.
        services.TryAddScoped<IWorkflowAssignmentResolver, NullAssignmentResolver>();
        services.TryAddScoped<IWorkflowDueDateResolver, NullDueDateResolver>();
        services.TryAddScoped<IWorkflowAuthorizationPolicy, AllowAllAuthorizationPolicy>();

        // TryAdd, so AddActorResolver<T>() and AddSubjectResolver<T>() win regardless of
        // the order they are called in relative to AddTaskRouter().
        services.TryAddScoped<IWorkflowActorResolver, Runner.NullActorResolver>();
        services.TryAddScoped<IWorkflowSubjectResolver, Runner.NullSubjectResolver>();

        if (includeBuiltInTriggers)
        {
            services.AddScoped<IWorkflowTrigger, SetVariableTrigger>();
            services.AddScoped<IWorkflowTrigger, ReportProgressTrigger>();
            services.AddScoped<IWorkflowTrigger, NotifyTrigger>();
            services.AddScoped<IWorkflowTrigger, WebhookTrigger>();
        }

        return new WorkflowEngineBuilder(services);
    }
}

/// <summary>No deadline for anything. Used when the host supplies no resolver.</summary>
internal sealed class NullDueDateResolver : IWorkflowDueDateResolver
{
    public Task<DateTime?> ResolveAsync(
        WorkflowSubject subject,
        WorkflowTaskSnapshot task,
        CancellationToken ct = default) => Task.FromResult<DateTime?>(null);
}

/// <summary>Keeps whatever assignment it was given. Used when the host supplies none.</summary>
internal sealed class NullAssignmentResolver : IWorkflowAssignmentResolver
{
    public Task<WorkflowAssignment> ResolveAsync(
        string? roleKey,
        WorkflowAssignment current,
        WorkflowTaskSnapshot task,
        CancellationToken ct = default) => Task.FromResult(current);
}

/// <summary>
/// Permits everything. Used when the host registers no policy.
///
/// A named class rather than a null check at the call site: "this system has no
/// authorization" should be a greppable object, not an absence. It warns on first use, once
/// per process — enough that an unauthorized system announces itself in the log, while
/// staying quiet enough not to warn on every operation. The latch is deliberately
/// process-wide and not per-container: a second unauthorized container in the same process
/// will not warn again, which is a trade accepted because a host registers the engine once.
/// </summary>
internal sealed class AllowAllAuthorizationPolicy(
    ILogger<AllowAllAuthorizationPolicy> logger) : IWorkflowAuthorizationPolicy
{
    private static int _warned;

    public Task<WorkflowAuthorizationResult> EvaluateAsync(
        WorkflowOperation operation,
        WorkflowAuthorizationContext context,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "No IWorkflowAuthorizationPolicy is registered: every workflow operation "
                + "is permitted for every actor. Register one with "
                + "AddTaskRouter().AddAuthorizationPolicy<T>() to change this.");
        }

        return Task.FromResult(WorkflowAuthorizationResult.Allowed);
    }
}
