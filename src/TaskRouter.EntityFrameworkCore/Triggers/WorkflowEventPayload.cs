using TaskRouter.Core.Abstractions;

namespace TaskRouter.EntityFrameworkCore.Triggers;

internal sealed record WorkflowEventPayload(int Version, WorkflowTaskSnapshot Task, BranchContext? Branch);
