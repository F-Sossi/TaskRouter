namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Where the assignment handed to the engine's assignment resolution came from, which
/// decides whether <see cref="Core.Model.WorkflowDefinitionVersion.CarryAssignmentForward"/>
/// has any say over it.
///
/// <para>The distinction exists because one value — "who the next task goes to when it
/// names nobody and carries no role" — arrives by two very different routes. A person
/// delegating a chain to a named colleague and a task simply following the last one look
/// identical by the time they reach the resolver, and only the first is a decision.</para>
///
/// <para>Required rather than defaulted on purpose. A new creation path that forgets to
/// say which it is would otherwise pick up whichever default happened to be there, and the
/// failure is silent: work quietly assigned to somebody who never asked for it, or quietly
/// taken away from somebody who was promised it.</para>
/// </summary>
internal enum AssignmentOrigin
{
    /// <summary>
    /// Somebody chose this assignment for this task: a delegation, an ad-hoc task raised
    /// against a person, a run started on a named actor, rework returned to whoever worked
    /// the branch. Always honoured.
    /// </summary>
    Explicit,

    /// <summary>
    /// Nobody chose it — it is whatever the previous task in the chain happened to hold.
    /// This is what the workflow's carry-forward setting governs, and by default it is
    /// dropped so the step arrives unassigned in its section.
    /// </summary>
    Inherited
}
