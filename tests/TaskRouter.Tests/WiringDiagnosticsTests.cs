using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Data;

using TaskRouter.Core.Abstractions;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// Whether a host wired the engine up correctly, answered at startup instead of at the
/// first request.
///
/// <para>This exists because of how the mistakes used to present. Forgetting the
/// <see cref="IWorkflowDbContext"/> registration failed on the first engine call as a
/// generic "no service for type"; a route condition with no evaluator skipped the route and
/// logged a warning nobody was watching; no authorization policy meant every operation was
/// permitted, silently. Each one is obvious once known and invisible until then, which is
/// the definition of something a tool should check.</para>
///
/// <para>The rule throughout: <b>report everything wrong at once, name the fix, and say
/// what will happen if it is ignored.</b> A diagnostic that reports the first problem only
/// turns one wrong startup into five.</para>
/// </summary>
[TestClass]
public class WiringDiagnosticsTests
{
    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DemoDbContext>(o => o.UseSqlServer("Server=unused;Database=unused"));
        return services;
    }

    [TestMethod]
    public void A_missing_db_context_registration_is_reported_with_the_line_to_add()
    {
        // The one no DI extension can supply, and the one whose absence used to surface as
        // "no service for type IWorkflowDbContext" on the first engine call.
        var services = BaseServices();
        services.AddTaskRouter();

        var report = WorkflowWiring.Inspect(services.BuildServiceProvider());

        Assert.IsFalse(report.IsHealthy);
        Assert.IsTrue(
            report.Problems.Any(p => p.Contains("IWorkflowDbContext", StringComparison.Ordinal)),
            "the missing seam must be named: " + string.Join(" | ", report.Problems));
        Assert.IsTrue(
            report.Problems.Any(p => p.Contains("AddScoped<IWorkflowDbContext>", StringComparison.Ordinal)),
            "and the fix spelled out, not just the symptom");
    }

    [TestMethod]
    public void A_correctly_wired_host_reports_healthy()
    {
        var services = BaseServices();
        services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<DemoDbContext>());
        services.AddTaskRouter();

        var report = WorkflowWiring.Inspect(services.BuildServiceProvider());

        Assert.IsTrue(report.IsHealthy,
            "a wired host should be clean: " + string.Join(" | ", report.Problems));
    }

    [TestMethod]
    public void Permissive_defaults_are_reported_as_warnings_rather_than_problems()
    {
        // Registering no authorization policy is legal -- the engine permits everything and
        // says so once in the log. That is right for a prototype and wrong for production,
        // so it warns rather than fails: a diagnostic that refuses to start over a
        // deliberate choice gets switched off, and then catches nothing.
        var services = BaseServices();
        services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<DemoDbContext>());
        services.AddTaskRouter();

        var report = WorkflowWiring.Inspect(services.BuildServiceProvider());

        Assert.IsTrue(report.IsHealthy, "a permissive default must not block startup");
        Assert.IsTrue(
            report.Warnings.Any(w => w.Contains("IWorkflowAuthorizationPolicy", StringComparison.Ordinal)),
            "but it must be said out loud: " + string.Join(" | ", report.Warnings));
    }

    [TestMethod]
    public void Every_problem_is_reported_at_once()
    {
        // Not just the first. Fixing them one startup at a time is the failure mode this
        // whole diagnostic exists to remove.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTaskRouter();

        var report = WorkflowWiring.Inspect(services.BuildServiceProvider());

        Assert.IsFalse(report.IsHealthy);
        Assert.IsGreaterThan(0, report.Problems.Count);
    }

    [TestMethod]
    public void The_registration_check_runs_before_the_container_is_built()
    {
        // The one that has to beat ASP.NET Core's validate-on-build. In Development that
        // fires first on a missing IWorkflowDbContext and reports it as eight services that
        // could not be constructed, with the actual cause repeated inside each and named
        // nowhere -- so a check that waits for a built container never gets to speak.
        var services = BaseServices();
        services.AddTaskRouter();

        var report = WorkflowWiring.Inspect(services);   // IServiceCollection, not provider

        Assert.IsFalse(report.IsHealthy);
        Assert.IsTrue(
            report.Problems.Any(p => p.Contains("AddTaskRouterFor<YourDbContext>", StringComparison.Ordinal)),
            "it should offer the one-call fix first: " + string.Join(" | ", report.Problems));
    }

    [TestMethod]
    public void AddTaskRouterFor_registers_the_mapping_a_host_would_otherwise_write()
    {
        // The whole value of the typed overload: the registration nothing can infer is
        // inferred from the type argument, so it cannot be left out.
        var services = BaseServices();
        services.AddTaskRouterFor<DemoDbContext>();

        var report = WorkflowWiring.Inspect(services);

        Assert.IsTrue(report.IsHealthy,
            "naming the context should satisfy the check: " + string.Join(" | ", report.Problems));

        using var scope = services.BuildServiceProvider().CreateScope();
        Assert.IsNotNull(scope.ServiceProvider.GetService<IWorkflowDbContext>());
    }

    [TestMethod]
    public void The_report_formats_as_something_a_person_can_act_on()
    {
        var services = BaseServices();
        services.AddTaskRouter();

        var text = WorkflowWiring.Inspect(services.BuildServiceProvider()).ToString();

        StringAssert.Contains(text, "TaskRouter");
        StringAssert.Contains(text, "IWorkflowDbContext");
        Assert.Contains('\n', text, "a wall of one line is not a report");
    }
}
