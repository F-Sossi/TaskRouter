using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

// MSTEST0032 flags these assertions as always true. That is exactly the point:
// they are true today and the schema depends on them staying true. The analyzer
// cannot see the SQL index filter that encodes the same constants.
#pragma warning disable MSTEST0032

/// <summary>
/// Guards assumptions the schema hardcodes, which would otherwise fail silently.
/// </summary>
[TestClass]
public class SchemaGuardTests
{
    /// <summary>
    /// The convergence unique index in WorkflowModelBuilder filters on
    /// `[Status] &lt;&gt; 3`, the ordinal of Cancelled. Reordering the enum would leave the
    /// index in place but change what it guards — silently weakening the protection
    /// against duplicate convergence tasks. Pin the ordinal.
    /// </summary>
    [TestMethod]
    public void Cancelled_status_ordinal_matches_the_convergence_index_filter()
    {
        Assert.AreEqual(3, (int)WorkflowTaskStatus.Cancelled,
            "The filtered unique index on WorkflowTasks.ForkManifestId hardcodes " +
            "[Status] <> 3 for Cancelled. Update WorkflowModelBuilder if this changes.");
    }

    /// <summary>
    /// Forked is terminal-but-not-cancelled, and the convergence index deliberately
    /// does not exclude it — a forked origin never carries a ForkManifestId.
    ///
    /// <para>Two filtered indexes in WorkflowModelBuilder spell these numbers out, because
    /// a SQL filter cannot name an enum member: the convergence guard excludes
    /// <c>[Status] &lt;&gt; 3</c>, and the inbox index covers <c>[Status] IN (0, 1)</c> —
    /// the open statuses. Renumbering would leave both indexes in place and silently
    /// change what they mean, which is why the whole enum is pinned rather than just the
    /// members in use.</para>
    /// </summary>
    [TestMethod]
    public void Task_status_ordinals_are_stable()
    {
        // 0 and 1 are the open statuses the inbox index filters on.
        Assert.AreEqual(0, (int)WorkflowTaskStatus.NotStarted);
        Assert.AreEqual(1, (int)WorkflowTaskStatus.InProgress);
        Assert.AreEqual(2, (int)WorkflowTaskStatus.Completed);
        Assert.AreEqual(3, (int)WorkflowTaskStatus.Cancelled);
        Assert.AreEqual(4, (int)WorkflowTaskStatus.Forked);
    }

    /// <summary>
    /// Trigger events are persisted as ints on TriggerDefinition and TriggerExecution, so
    /// inserting a member mid-list renumbers everything after it and silently rewrites what
    /// stored rows mean — a row saying Custom would come back as its new neighbour.
    ///
    /// New members go on the end. If this test fails, move the member rather than updating
    /// the numbers, unless every consumer's data is being migrated deliberately.
    /// </summary>
    [TestMethod]
    public void Event_kind_ordinals_are_stable()
    {
        Assert.AreEqual(0, (int)WorkflowEventKind.RunStarted);
        Assert.AreEqual(1, (int)WorkflowEventKind.RunCompleted);
        Assert.AreEqual(2, (int)WorkflowEventKind.TaskCreated);
        Assert.AreEqual(3, (int)WorkflowEventKind.TaskAssigned);
        Assert.AreEqual(4, (int)WorkflowEventKind.TaskCompleted);
        Assert.AreEqual(5, (int)WorkflowEventKind.TaskCancelled);
        Assert.AreEqual(6, (int)WorkflowEventKind.TaskForked);
        Assert.AreEqual(7, (int)WorkflowEventKind.BranchAdded);
        Assert.AreEqual(8, (int)WorkflowEventKind.BranchCompleted);
        Assert.AreEqual(9, (int)WorkflowEventKind.ForkConverged);
        Assert.AreEqual(10, (int)WorkflowEventKind.SelectiveRejection);
        Assert.AreEqual(11, (int)WorkflowEventKind.SubWorkflowStarted);
        Assert.AreEqual(12, (int)WorkflowEventKind.SubWorkflowCompleted);
        Assert.AreEqual(13, (int)WorkflowEventKind.Custom);
        Assert.AreEqual(14, (int)WorkflowEventKind.SubWorkflowCancelled);
        Assert.AreEqual(15, (int)WorkflowEventKind.TaskDueSoon);
        Assert.AreEqual(16, (int)WorkflowEventKind.TaskOverdue);
    }

    /// <summary>
    /// Sub-workflow status is persisted too, and the blocking guard tests for Running.
    /// </summary>
    [TestMethod]
    public void Sub_workflow_status_ordinals_are_stable()
    {
        Assert.AreEqual(0, (int)SubWorkflowStatus.Running);
        Assert.AreEqual(1, (int)SubWorkflowStatus.Completed);
        Assert.AreEqual(2, (int)SubWorkflowStatus.Cancelled);
    }

    /// <summary>
    /// Two version-wide attachments of the same sub-workflow in the same version must be
    /// impossible. EF's SQL Server provider filters nullable columns out of unique indexes
    /// unless told not to, which would silently allow exactly that.
    ///
    /// Invisible in the entity and in the model builder's HasIndex call alike — the filter
    /// is a provider convention, so only the built model shows whether it is there. A
    /// regenerated migration would carry it back in without anything else failing.
    ///
    /// No connection is made: building the model is enough.
    /// </summary>
    [TestMethod]
    public void Version_wide_attachments_are_unique_per_sub_workflow()
    {
        var options = new DbContextOptionsBuilder<DemoDbContext>()
            .UseSqlServer($"{TestHost.BaseConnectionString};Database=WorkflowSchemaGuard")
            .Options;

        using var db = new DemoDbContext(options);

        var index = db.Model
            .FindEntityType(typeof(SubWorkflowAttachment))!
            .GetIndexes()
            .Single(i => i.IsUnique && i.Properties.Count == 3);

        Assert.IsNull(index.GetFilter(),
            "A filter here would exclude version-wide rows from the uniqueness guarantee. " +
            "HasFilter(null) in WorkflowModelBuilder is what keeps it off.");
    }
}

#pragma warning restore MSTEST0032
