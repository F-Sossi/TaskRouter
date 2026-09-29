namespace TaskRouter.EntityFrameworkCore.Builder;

/// <summary>
/// What the builder needs from the host that the engine cannot work out for itself.
///
/// <para>Only one thing qualifies. Assignment role keys are opaque to the engine — a host's
/// <c>IWorkflowAssignmentResolver</c> interprets them and the engine never looks inside one —
/// so there is nothing to enumerate and the builder has to be told. Every other choice it
/// offers an author is derived from what is registered or stored: task types from the
/// database, trigger descriptors from the trigger registry, route conditions from the
/// registered evaluators, sub-workflows from the definitions marked as such.</para>
/// </summary>
public sealed class WorkflowBuilderOptions
{
    /// <summary>
    /// The role keys this host's assignment resolver understands, offered to an author as the
    /// choices for "who should this step go to".
    ///
    /// <para>Empty means a workflow can still be authored — every step simply inherits the
    /// previous step's assignment, which is what an unset role key does anyway.</para>
    /// </summary>
    public IReadOnlyList<string> AssignmentRoles { get; set; } = [];

    /// <summary>
    /// The subject types a workflow may be authored against — the host's document types,
    /// case types, whatever it attaches workflows to.
    ///
    /// <para>Opaque to the engine for the same reason role keys are: a subject type is the
    /// host's word, matched by ordinary string comparison when a run starts. So the engine
    /// cannot enumerate them and the builder has to be told, or an author types the string by
    /// hand and a single character's difference produces a workflow that is never offered on
    /// any document — with nothing on screen to say why.</para>
    ///
    /// <para>Empty keeps the free-text field, which is right for a host that has not settled
    /// its subject types yet.</para>
    /// </summary>
    public IReadOnlyList<string> SubjectTypes { get; set; } = [];

    /// <summary>
    /// The outcome keys an author may give a task, if the host has a fixed set of them.
    ///
    /// <para>The third of these lists and the one with the sharpest failure. An outcome key
    /// is what a route matches on, so a key nobody routes on is a step that completes and
    /// then stops the workflow dead — the validator catches that for a non-terminal task.
    /// What it cannot catch is a <b>host</b> that maps outcome keys onto a vocabulary of its
    /// own: the first host has a closed set of eight, and one workflow using a key outside it
    /// made an entire document's task list fail with a 500 rather than showing the tasks.</para>
    ///
    /// <para>Empty keeps the free-text field, which is right for a host whose outcomes are
    /// genuinely open — an outcome key is the engine's own concept, unlike a subject type, so
    /// a host that never translates them has nothing to constrain.</para>
    /// </summary>
    public IReadOnlyList<string> Outcomes { get; set; } = [];
}

/// <summary>
/// Who is editing a workflow.
///
/// <para>Separate from <see cref="WorkflowBuilderOptions"/> because it is a per-request answer
/// where the roles are configuration. Every row the builder writes is attributed to this actor,
/// so a host with no authentication still has to answer — and answering "nobody" is a decision
/// worth making deliberately rather than by leaving a field null.</para>
/// </summary>
public interface IWorkflowEditorActorAccessor
{
    /// <summary>The acting editor's id. Never null or blank.</summary>
    string ActorId { get; }
}

/// <summary>
/// The accessor a host gets if it registers none: it refuses, and says what to register.
///
/// <para>A default that returned a placeholder would attribute every workflow edit in the
/// system to a fiction, and nothing would look wrong until somebody asked who changed a
/// workflow and found out nobody had.</para>
/// </summary>
internal sealed class UnconfiguredEditorActorAccessor : IWorkflowEditorActorAccessor
{
    public string ActorId => throw new InvalidOperationException(
        "No IWorkflowEditorActorAccessor is registered, so workflow edits cannot be attributed "
        + "to anybody. Register one alongside AddWorkflowBuilder() — it answers who is editing, "
        + "which the engine has no way to know.");
}
