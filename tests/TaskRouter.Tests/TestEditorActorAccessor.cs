using TaskRouter.EntityFrameworkCore.Builder;

namespace TaskRouter.Tests;

/// <summary>Who the builder attributes edits to in tests.</summary>
internal sealed class TestEditorActorAccessor : IWorkflowEditorActorAccessor
{
    public string ActorId => "builder";
}
