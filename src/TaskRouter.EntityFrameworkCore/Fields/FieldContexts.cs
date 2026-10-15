using Microsoft.Extensions.DependencyInjection;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Fields;

/// <summary>What a target's resolver is given to find its records.</summary>
public sealed class FieldResolveContext(
    WorkflowSubject subject, WorkflowTaskSnapshot task, IServiceProvider services)
{
    public WorkflowSubject Subject { get; } = subject;

    /// <summary>The task whose event fired the trigger — its branch key finds "this section's" records.</summary>
    public WorkflowTaskSnapshot Task { get; } = task;

    public IServiceProvider Services { get; } = services;

    /// <summary>
    /// The host's context, as the same instance the engine is writing through. Resolving
    /// the concrete context type from the container would give the same scoped instance in
    /// every host wired by <c>AddTaskRouterFor</c>, but this cannot be got wrong.
    /// </summary>
    public TContext Db<TContext>() where TContext : class =>
        Services.GetRequiredService<IWorkflowDbContext>() as TContext
        ?? throw new InvalidOperationException(
            $"IWorkflowDbContext is not a {typeof(TContext).Name}.");
}

/// <summary>What a field's writer and a target's <c>OnWritten</c> hook are given.</summary>
public sealed class FieldWriteContext(string actorId, IServiceProvider services)
{
    /// <summary>Who caused the event. <see cref="WorkflowActors.System"/> for the deadline sweep.</summary>
    public string ActorId { get; } = actorId;

    public bool IsSystem => ActorId == WorkflowActors.System;

    public IServiceProvider Services { get; } = services;

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();
}

/// <summary>What a lookup field's options delegate is given.</summary>
public sealed class FieldOptionsContext(string? search, IServiceProvider services)
{
    /// <summary>What the author has typed so far. Null or empty means "the first few".</summary>
    public string? Search { get; } = search;

    public IServiceProvider Services { get; } = services;

    public TContext Db<TContext>() where TContext : class =>
        Services.GetRequiredService<IWorkflowDbContext>() as TContext
        ?? throw new InvalidOperationException(
            $"IWorkflowDbContext is not a {typeof(TContext).Name}.");
}

/// <summary>
/// Writes one field on one record. Throwing refuses the write — the message is what the
/// person completing the task sees, so make it a sentence about their document.
/// </summary>
public delegate Task FieldWriter<in TEntity, in TValue>(
    TEntity entity, TValue value, FieldWriteContext context, CancellationToken ct);
