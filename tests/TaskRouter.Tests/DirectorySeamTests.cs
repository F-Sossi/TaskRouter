using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// The two host seams the runner and inbox ask their questions through, and specifically
/// what happens when a host has not answered them.
///
/// <para>Both fail soft by design: a directory that is missing or broken should empty a
/// drop-down, not take down a run view. That is easy to state and easy to lose — a null
/// reference in the wrong place turns "the picker is empty" into "the page is an error" —
/// so it is tested rather than asserted in a comment.</para>
/// </summary>
[TestClass]
public class DirectorySeamTests
{
    /// <summary>A directory that fails at every question, for the fail-soft cases.</summary>
    private sealed class ThrowingDirectory : IWorkflowActorResolver
    {
        public Task<string?> GetDisplayNameAsync(string actorId, CancellationToken ct = default) =>
            throw new InvalidOperationException("The directory is down.");

        public Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("The directory is down.");

        public Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("The directory is down.");

        public Task<IReadOnlyList<string>> GetOrgUnitsForAsync(
            string actorId, CancellationToken ct = default) =>
            throw new InvalidOperationException("The directory is down.");

        public Task<WorkflowAssignment> GetAssignmentForAsync(
            string? actorId, CancellationToken ct = default) =>
            throw new InvalidOperationException("The directory is down.");
    }

    private static ServiceProvider Provider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        // Never connected to -- every test here is about what DI resolves and what the
        // defaults answer, and none of it reaches a database.
        services.AddLogging();
        services.AddDbContext<DemoDbContext>(o => o.UseSqlServer("Server=none"));
        services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<DemoDbContext>());
        services.AddTaskRouter();

        configure?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    [TestMethod]
    public async Task An_unregistered_directory_answers_emptily_rather_than_failing()
    {
        // The runner must still render for a host that has not wired a directory. An
        // empty reassign picker is a degraded page; an unresolved service is a broken one.
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var directory = scope.ServiceProvider.GetRequiredService<IWorkflowActorResolver>();

        Assert.IsEmpty(await directory.GetActorsAsync());
        Assert.IsEmpty(await directory.GetBranchOptionsAsync());
        Assert.IsEmpty(await directory.GetOrgUnitsForAsync("actor-1"),
            "No org units means nothing unclaimed is theirs -- work assigned by name "
            + "still reaches the inbox, so this is honest rather than broken.");
        Assert.IsNull(await directory.GetDisplayNameAsync("actor-1"));
    }

    [TestMethod]
    public async Task The_default_directory_keeps_the_actor_even_though_it_has_no_org_unit()
    {
        // The one place the null default must not answer "nothing". Discarding the actor
        // would silently unassign work on every path that routes through the directory,
        // which is a great deal worse than a missing branch key -- and a missing branch key
        // already has a defined meaning: keep the current assignment.
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var directory = scope.ServiceProvider.GetRequiredService<IWorkflowActorResolver>();
        var assignment = await directory.GetAssignmentForAsync("actor-1");

        Assert.AreEqual("actor-1", assignment.ActorId, "The actor was dropped, not just their unit.");
        Assert.IsNull(assignment.BranchKey);

        Assert.AreEqual(
            WorkflowAssignment.Unassigned,
            await directory.GetAssignmentForAsync(null),
            "No actor is genuinely unassigned, which is a different answer.");
    }

    [TestMethod]
    public async Task An_unregistered_subject_resolver_resolves_nothing_rather_than_failing()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var subjects = scope.ServiceProvider.GetRequiredService<IWorkflowSubjectResolver>();

        Assert.IsEmpty(await subjects.ResolveAsync([new WorkflowSubject("ChangeRequest", "42")]),
            "An empty dictionary is the documented 'could not resolve' answer, so every "
            + "row falls back to its raw key.");
    }

    [TestMethod]
    public void AddActorResolver_beats_the_default_whichever_order_they_are_called_in()
    {
        // The defaults go in through TryAddScoped, so this is a real ordering guarantee and
        // getting it backwards would silently give every host the null directory -- with
        // empty pickers and one log line nobody reads.
        using var after = Provider(s => s.AddScoped<IWorkflowActorResolver, DemoDirectory>());
        using var afterScope = after.CreateScope();

        Assert.IsInstanceOfType<DemoDirectory>(
            afterScope.ServiceProvider.GetRequiredService<IWorkflowActorResolver>(),
            "A directory registered after AddTaskRouter() did not win.");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DemoDbContext>(o => o.UseSqlServer("Server=none"));
        services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<DemoDbContext>());
        services.AddScoped<IWorkflowActorResolver, DemoDirectory>();
        services.AddTaskRouter();

        using var before = services.BuildServiceProvider(validateScopes: true);
        using var beforeScope = before.CreateScope();

        Assert.IsInstanceOfType<DemoDirectory>(
            beforeScope.ServiceProvider.GetRequiredService<IWorkflowActorResolver>(),
            "A directory registered before AddTaskRouter() was overwritten by the default.");
    }

    [TestMethod]
    public async Task A_directory_that_throws_is_still_a_directory_the_caller_can_call()
    {
        // Documents the contract rather than the implementation: the seam itself does not
        // catch, so every *caller* in the library must. The runner client's tests are where
        // that is proved end to end; this exists so the expectation is written down next to
        // the interface it constrains.
        var directory = new ThrowingDirectory();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => directory.GetActorsAsync());
    }
}
