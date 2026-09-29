using TaskRouter.EntityFrameworkCore.Builder;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Who is editing a workflow, for a demo that has no authentication.
///
/// <para>A real host returns the signed-in user. This returns a fixed id and names it plainly,
/// rather than something that reads like a person — every workflow edit in the demo is
/// attributed to it, and it should be obvious in the audit trail that nobody was identified.</para>
/// </summary>
public sealed class DemoEditorActorAccessor : IWorkflowEditorActorAccessor
{
    public string ActorId => "demo:builder";
}
