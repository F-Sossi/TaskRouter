using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// What a host has to write to satisfy <see cref="IWorkflowDbContext"/>.
///
/// <para>The answer should be "nothing but the declaration". Every member the interface
/// needs is either one <c>DbContext</c> already has — <c>Set&lt;T&gt;()</c>,
/// <c>Database</c>, <c>SaveChangesAsync</c> — or a property defaulted on the interface in
/// terms of <c>Set&lt;T&gt;()</c>. Nineteen hand-written <c>DbSet</c> properties were the
/// single largest mechanical cost of adopting this library, and they carried a second cost:
/// a host with a type or member of its own by one of those names had to work around the
/// collision.</para>
///
/// <para>These tests are the guard on that. They are compile-time assertions as much as
/// runtime ones — if the interface grows a member <c>DbContext</c> does not supply and that
/// is not defaulted, <see cref="BareHostContext"/> stops compiling and the cost lands back
/// on every host.</para>
/// </summary>
[TestClass]
public class HostContextShapeTests
{
    /// <summary>
    /// A host context in full. The empty body is the entire point: declaring the interface
    /// is all that is asked of a host.
    /// </summary>
    private sealed class BareHostContext(DbContextOptions<BareHostContext> options)
        : DbContext(options), IWorkflowDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureTaskRouter();
    }

    /// <summary>
    /// A host that already has a member by one of the interface's names. It declares its
    /// own, which wins over the default — and, unlike before, it is not forced to declare
    /// the other eighteen to get there.
    /// </summary>
    private sealed class CollidingHostContext(DbContextOptions<CollidingHostContext> options)
        : DbContext(options), IWorkflowDbContext
    {
        public DbSet<TriggerDefinition> WorkflowTriggerDefinitions => Set<TriggerDefinition>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureTaskRouter();
    }

    [TestMethod]
    public void A_host_context_needs_no_members_of_its_own()
    {
        // Reaching the sets through the interface is what the engine does, so this is the
        // access path that has to work -- and the one the defaults serve.
        using var context = new BareHostContext(
            new DbContextOptionsBuilder<BareHostContext>()
                .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True")
                .Options);

        IWorkflowDbContext db = context;

        // Each is a defaulted property resolving to Set<T>(); a null here would mean the
        // default was dropped rather than inherited.
        Assert.IsNotNull(db.WorkflowTasks);
        Assert.IsNotNull(db.WorkflowRuns);
        Assert.IsNotNull(db.WorkflowDefinitions);
        Assert.IsNotNull(db.WorkflowDefinitionVersions);
        Assert.IsNotNull(db.WorkflowTaskDefinitions);
        Assert.IsNotNull(db.WorkflowTaskOutcomes);
        Assert.IsNotNull(db.WorkflowTaskRoutes);
        Assert.IsNotNull(db.WorkflowTaskTypes);
        Assert.IsNotNull(db.WorkflowOutcomeTypes);
        Assert.IsNotNull(db.WorkflowPreAssignments);
        Assert.IsNotNull(db.WorkflowTriggerDefinitions);
        Assert.IsNotNull(db.WorkflowSubWorkflowAttachments);
        Assert.IsNotNull(db.WorkflowSubWorkflowInstances);
        Assert.IsNotNull(db.WorkflowForkManifests);
        Assert.IsNotNull(db.WorkflowForkManifestEntries);
        Assert.IsNotNull(db.WorkflowVariables);
        Assert.IsNotNull(db.WorkflowTaskLogs);
        Assert.IsNotNull(db.WorkflowTriggerExecutions);
        Assert.IsNotNull(db.WorkflowOutbox);
        Assert.IsNotNull(db.Database);
    }

    [TestMethod]
    public void A_defaulted_set_is_the_same_set_the_context_tracks()
    {
        // The defaults must not hand back some parallel set. Set<T>() returns the context's
        // own set, so the engine writing through the interface and the host writing through
        // its context share one change tracker -- which is the reason the engine takes the
        // host's context at all.
        using var context = new BareHostContext(
            new DbContextOptionsBuilder<BareHostContext>()
                .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True")
                .Options);

        IWorkflowDbContext db = context;

        Assert.AreSame(context.Set<WorkflowTask>().EntityType, db.WorkflowTasks.EntityType);

        // The decisive one: something added through the interface is tracked by the host's
        // own context. If the defaults handed back a detached set, this count stays zero and
        // engine writes would never join the host's transaction.
        db.WorkflowTasks.Add(new WorkflowTask
        {
            WorkflowRunId = 1, TaskDefinitionId = 1, CreatorId = "t", ModifierId = "t"
        });

        Assert.AreEqual(1, context.ChangeTracker.Entries<WorkflowTask>().Count());
    }

    [TestMethod]
    public void A_host_may_declare_one_of_its_own_without_declaring_them_all()
    {
        // The collision case. Before the defaults, a host whose own context already had a
        // member by one of these names could not declare it implicitly -- and the workaround
        // dragged in all nineteen. Now it declares the one and inherits the rest.
        using var context = new CollidingHostContext(
            new DbContextOptionsBuilder<CollidingHostContext>()
                .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True")
                .Options);

        IWorkflowDbContext db = context;

        Assert.IsNotNull(db.WorkflowTriggerDefinitions, "the host's own member satisfies it");
        Assert.IsNotNull(db.WorkflowTasks, "and the other eighteen still come from the defaults");
    }
}
