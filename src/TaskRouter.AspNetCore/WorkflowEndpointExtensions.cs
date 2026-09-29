using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TaskRouter.AspNetCore;

public static class WorkflowEndpointExtensions
{
    /// <summary>
    /// Registers the endpoint layer's seams and their defaults.
    ///
    /// Every registration is <c>TryAdd</c>, so a host that registered its own accessor
    /// or resolver first keeps it. Call this alongside <c>AddTaskRouter()</c>, not
    /// instead of it — this layer adds HTTP, not the engine.
    /// </summary>
    public static IServiceCollection AddTaskRouterEndpoints(
        this IServiceCollection services,
        Action<WorkflowEndpointOptions>? configure = null)
    {
        var options = new WorkflowEndpointOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton<IWorkflowEndpointActorAccessor, ClaimsActorAccessor>();
        services.TryAddScoped<IWorkflowEndpointBranchKeyResolver, NoBranchKeysResolver>();
        services.TryAddSingleton<WorkflowResultMapper>();

        return services;
    }

    /// <summary>
    /// Maps every <c>IWorkflowEngine</c> operation under <paramref name="prefix"/>.
    ///
    /// Returns the group so the host applies its own policy — this package deliberately
    /// does not call <c>RequireAuthorization</c>, because it does not know the host's
    /// scheme. It is safe without one: the actor accessor fails closed, so an
    /// unauthenticated request is refused with 401 whatever the host forgot.
    ///
    /// <para><b>The builder routes are the exception to that last sentence</b>, and are mounted
    /// under <c>builder/</c> so a host can tell them apart. They edit workflow *definitions*
    /// rather than acting on a task, so they take no actor and the accessor does not gate them —
    /// and changing a definition changes how every future run of it behaves. They are also inert
    /// unless <c>AddWorkflowBuilder()</c> registered a client, so a host that never adopted the
    /// builder is unaffected by their presence.</para>
    ///
    /// <para><paramref name="configureBuilderGroup"/> exists for exactly that asymmetry: it
    /// hands the host the builder subgroup so it can carry a stricter policy than the rest.
    /// Editing a definition is a different privilege from completing a task, and a host that
    /// could only gate the whole group at once would have to choose between locking everybody
    /// out of their own inbox and leaving authoring open to anybody who can complete a
    /// task.</para>
    /// </summary>
    /// <param name="endpoints">Where to map.</param>
    /// <param name="prefix">The route prefix. Must match any HTTP client's prefix.</param>
    /// <param name="configureBuilderGroup">
    /// Applied to the <c>builder/</c> subgroup only, before the host's own policy is applied
    /// to the returned group. Typically <c>group => group.RequireAuthorization("…")</c>.
    /// </param>
    public static RouteGroupBuilder MapTaskRouterEndpoints(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/workflow",
        Action<RouteGroupBuilder>? configureBuilderGroup = null)
    {
        var group = endpoints.MapGroup(prefix);

        group.MapTaskEndpoints();
        group.MapRunEndpoints();
        group.MapReadEndpoints();
        group.MapInboxEndpoints();
        group.MapRunnerEndpoints();

        // Mapped first and unconditionally. Written as
        // configureBuilderGroup?.Invoke(group.MapBuilderEndpoints()) the argument is not
        // evaluated when the callback is null -- that is what ?. means -- so every host
        // that did not pass one would silently lose the builder routes entirely.
        var builderGroup = group.MapBuilderEndpoints();

        configureBuilderGroup?.Invoke(builderGroup);

        return group;
    }
}
