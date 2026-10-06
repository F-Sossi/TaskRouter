using Microsoft.Extensions.DependencyInjection;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.EntityFrameworkCore.Builder;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Checks that a host has wired the engine up, and says what is wrong in terms of what to
/// add rather than what threw.
///
/// <para><b>Why this exists.</b> Every way of getting the registration wrong used to fail
/// at the first request rather than at startup, and none of the errors named the cause.
/// Omitting the <see cref="IWorkflowDbContext"/> mapping produced a generic "no service for
/// type"; a route condition with no evaluator skipped the route and logged a warning nobody
/// was watching; registering no authorization policy permitted every operation silently.
/// Each is obvious once you know it and invisible until then — which is exactly the sort of
/// thing a program should check rather than a person remember.</para>
///
/// <para>Call <see cref="Inspect(IServiceProvider)"/> yourself, or let
/// <c>AddTaskRouter().ValidateWiringAtStartup()</c> run it as the application starts.</para>
/// </summary>
public static class WorkflowWiring
{
    /// <summary>
    /// What the inspection found. <see cref="Problems"/> will stop the engine working;
    /// <see cref="Warnings"/> are legal choices worth knowing you made.
    /// </summary>
    public sealed record Report(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
    {
        /// <summary>No problems. Warnings do not make a host unhealthy — see
        /// <see cref="Warnings"/> for why that distinction is deliberate.</summary>
        public bool IsHealthy => Problems.Count == 0;

        public override string ToString()
        {
            if (IsHealthy && Warnings.Count == 0)
            {
                return "TaskRouter wiring: no problems found.";
            }

            var lines = new List<string>();

            if (Problems.Count > 0)
            {
                lines.Add("TaskRouter is not correctly wired:");
                lines.AddRange(Problems.Select(p => "  * " + p.Replace("\n", "\n    ")));
            }

            if (Warnings.Count > 0)
            {
                lines.Add(Problems.Count > 0 ? "" : "TaskRouter wiring:");
                lines.Add("Worth knowing:");
                lines.AddRange(Warnings.Select(w => "  - " + w.Replace("\n", "\n    ")));
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Checks the registrations <b>before the container is built</b>, by reading the
    /// descriptors rather than resolving anything.
    ///
    /// <para>This exists because of where the competition is. ASP.NET Core validates the
    /// container on build in Development, and a missing <see cref="IWorkflowDbContext"/>
    /// trips that first — producing an <c>AggregateException</c> naming all eight services
    /// that could not be constructed, with the single actual cause buried inside each. A
    /// check that runs after the host is built never gets to speak. This one runs at
    /// registration time, so it is the first thing the author sees.</para>
    ///
    /// <para>It can only see what is registered, not whether it works — so it answers the
    /// "did you forget a line" questions, and <see cref="Inspect(IServiceProvider)"/>
    /// answers the rest once there is a container to ask.</para>
    /// </summary>
    public static Report Inspect(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var problems = new List<string>();
        var warnings = new List<string>();

        bool Registered<T>() => services.Any(d => d.ServiceType == typeof(T));

        if (!Registered<IWorkflowDbContext>())
        {
            problems.Add(
                "IWorkflowDbContext is not registered, so no engine call can reach a database.\n"
                + "Either name your context when registering the engine:\n"
                + "    services.AddTaskRouterFor<YourDbContext>();\n"
                + "or map it yourself, after your AddDbContext call:\n"
                + "    services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<YourDbContext>());");
        }

        if (!Registered<IWorkflowEngine>())
        {
            problems.Add(
                "The engine is not registered. Add:\n"
                + "    services.AddTaskRouter();   // or AddTaskRouterFor<YourDbContext>()");
        }

        if (Registered<IWorkflowBuilderClient>() && !Registered<IWorkflowEditorActorAccessor>())
        {
            problems.Add(
                "The workflow builder is registered but nothing says who is editing, so every "
                + "save would be misattributed.\n"
                + "    services.AddScoped<IWorkflowEditorActorAccessor, YourAccessor>();");
        }

        if (!Registered<IWorkflowAuthorizationPolicy>())
        {
            warnings.Add(
                "No IWorkflowAuthorizationPolicy is registered, so every operation is permitted "
                + "for every actor. Register one with .AddAuthorizationPolicy<T>() before this "
                + "reaches production.");
        }

        if (!Registered<IWorkflowAssignmentResolver>())
        {
            warnings.Add(
                "No IWorkflowAssignmentResolver is registered, so role keys on a task definition "
                + "resolve to nothing and tasks keep whatever assignment they were handed. "
                + "Register one with .AddAssignmentResolver<T>().");
        }

        if (!Registered<IWorkflowDueDateResolver>())
        {
            warnings.Add(
                "No IWorkflowDueDateResolver is registered, so no task is ever given a deadline "
                + "and reminders and escalation stay inert.");
        }

        return new Report(problems, warnings);
    }

    /// <summary>
    /// Resolves what the engine needs and reports everything missing at once.
    ///
    /// <para>Everything, deliberately — reporting only the first problem turns one wrong
    /// startup into five. Resolution happens inside a scope because the seams are scoped.</para>
    /// </summary>
    public static Report Inspect(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var problems = new List<string>();
        var warnings = new List<string>();

        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;

        // Resolution itself can throw, and the cases where it does are exactly the ones this
        // method exists to report: a registered service whose own dependency is missing
        // throws rather than returning null, so asking for the engine when
        // IWorkflowDbContext is absent brings down the diagnostic instead of describing the
        // problem. Everything below goes through here.
        T? Resolve<T>() where T : class
        {
            try
            {
                return services.GetService<T>();
            }
            catch
            {
                return null;
            }
        }

        // ── The one no DI extension can supply ──
        //
        // AddTaskRouter() has no way to know which of the host's contexts implements the
        // interface, so this is the single registration every host must write itself. It is
        // also the one whose absence used to be hardest to read.
        if (Resolve<IWorkflowDbContext>() is null)
        {
            problems.Add(
                "IWorkflowDbContext is not registered, so no engine call can reach a database.\n"
                + "Add, after your AddDbContext call:\n"
                + "    services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<YourDbContext>());");
        }

        // ── The engine itself ──
        // Only worth saying when the context is there -- otherwise the engine is
        // unresolvable *because* of the problem already reported above, and naming it twice
        // sends somebody looking for a second fault that does not exist.
        if (problems.Count == 0 && Resolve<IWorkflowEngine>() is null)
        {
            problems.Add(
                "The engine is not registered. Add:\n"
                + "    services.AddTaskRouter();");
        }

        // ── Legal, but worth saying out loud ──
        //
        // Warnings rather than problems on purpose. Each of these is a working default and a
        // reasonable choice while getting started; failing startup over a deliberate choice
        // gets the whole diagnostic switched off, and then it catches nothing.
        if (Resolve<IWorkflowAuthorizationPolicy>() is null or AllowAllAuthorizationPolicy)
        {
            warnings.Add(
                "No IWorkflowAuthorizationPolicy is registered, so every operation is permitted "
                + "for every actor. Register one with .AddAuthorizationPolicy<T>() before this "
                + "reaches production.");
        }

        if (Resolve<IWorkflowAssignmentResolver>() is null or NullAssignmentResolver)
        {
            warnings.Add(
                "No IWorkflowAssignmentResolver is registered, so tasks keep whatever assignment "
                + "they were handed and role keys on a task definition resolve to nothing. "
                + "Register one with .AddAssignmentResolver<T>().");
        }

        if (Resolve<IWorkflowDueDateResolver>() is null or NullDueDateResolver)
        {
            warnings.Add(
                "No IWorkflowDueDateResolver is registered, so no task is ever given a deadline "
                + "and reminders and escalation stay inert.");
        }

        // ── The builder's own seam ──
        //
        // IWorkflowEditorActorAccessor throws rather than defaulting, because attributing
        // every workflow edit in a system to a placeholder is worse than failing. Resolving
        // it here turns that into a startup message instead of a first-edit exception.
        if (Resolve<IWorkflowBuilderClient>() is not null)
        {
            try
            {
                _ = services.GetRequiredService<IWorkflowEditorActorAccessor>().ActorId;
            }
            catch (Exception ex)
            {
                problems.Add(
                    "The workflow builder is registered but IWorkflowEditorActorAccessor cannot "
                    + "say who is editing, so every save would be misattributed or throw.\n"
                    + "Register one with services.AddScoped<IWorkflowEditorActorAccessor, YourAccessor>().\n"
                    + "It reported: " + ex.Message);
            }
        }

        return new Report(problems, warnings);
    }
}
