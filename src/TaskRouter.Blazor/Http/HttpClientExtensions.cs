using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TaskRouter.Core.Builder;
using TaskRouter.Core.Inbox;
using TaskRouter.Core.Runner;

namespace TaskRouter.Blazor.Http;

/// <summary>
/// Registration for the HTTP clients.
///
/// <para><b>Neither overload uses <c>IHttpClientFactory</c>.</b> That would add a
/// <c>Microsoft.Extensions.Http</c> dependency to a package whose reason to exist is running
/// in a browser, where the factory's pooling buys nothing — the handler is the browser's
/// fetch, and there is no socket to exhaust or DNS entry to go stale. A host that wants a
/// typed client through the factory can register one itself; <see cref="HttpWorkflowBuilderClient"/>
/// takes an <c>HttpClient</c> and holds no other state.</para>
/// </summary>
public static class HttpClientExtensions
{
    /// <summary>
    /// Registers <see cref="IWorkflowBuilderClient"/> over the <c>HttpClient</c> the host has
    /// already registered — which the Blazor WebAssembly template does, pointed at the host
    /// environment's base address.
    ///
    /// <para><paramref name="prefix"/> must match the prefix that server passed to
    /// <c>MapTaskRouterEndpoints</c>. The two are a pair; the defaults agree, so a host that
    /// changed neither can ignore this.</para>
    ///
    /// <para><b>A Blazor Server host does not want this.</b> It registers
    /// <c>AddWorkflowBuilder()</c> from the EF package and talks to the engine directly.
    /// Registering this instead adds a network round trip, a serialization pass and a second
    /// set of failure modes to what was a method call — and it will work, which is what makes
    /// it worth saying out loud.</para>
    /// </summary>
    public static IServiceCollection AddTaskRouterBuilderHttpClient(
        this IServiceCollection services,
        string prefix = "/workflow")
    {
        services.AddScoped<IWorkflowBuilderClient>(sp =>
            new HttpWorkflowBuilderClient(sp.GetRequiredService<HttpClient>(), prefix));

        return services;
    }

    /// <summary>
    /// The same, for a host with no <c>HttpClient</c> of its own: registers one as a
    /// singleton at <paramref name="baseAddress"/>.
    ///
    /// <para><c>TryAddSingleton</c>, so a host that already registered an <c>HttpClient</c>
    /// keeps it and this call configures nothing — silently replacing a host's own client,
    /// with its handlers and headers, would be a poor trade for a convenience overload.
    /// Singleton rather than scoped because in a Blazor Server host a scope is a circuit,
    /// and one <c>HttpClient</c> per connected user is how a host runs out of sockets.</para>
    /// </summary>
    public static IServiceCollection AddTaskRouterBuilderHttpClient(
        this IServiceCollection services,
        Uri baseAddress,
        string prefix = "/workflow")
    {
        services.TryAddSingleton(_ => new HttpClient { BaseAddress = baseAddress });

        return services.AddTaskRouterBuilderHttpClient(prefix);
    }

    /// <summary>
    /// Registers <see cref="IWorkflowRunnerClient"/> and <see cref="IWorkflowInboxClient"/>
    /// over the <c>HttpClient</c> the host has already registered, for a WebAssembly host
    /// driving workflows in a browser.
    ///
    /// <para>Both together, because the runner and the inbox are two views of the same work
    /// and no host has yet wanted one without the other. Register the concrete classes
    /// directly if that changes.</para>
    ///
    /// <para><paramref name="prefix"/> must match the prefix that server passed to
    /// <c>MapTaskRouterEndpoints</c>.</para>
    ///
    /// <para><b>A Blazor Server host does not want this</b> — it registers
    /// <c>AddWorkflowRunner()</c> from the EF package and talks to the engine directly.</para>
    /// </summary>
    public static IServiceCollection AddTaskRouterRunnerHttpClient(
        this IServiceCollection services,
        string prefix = "/workflow")
    {
        services.AddScoped<IWorkflowRunnerClient>(sp =>
            new HttpWorkflowRunnerClient(sp.GetRequiredService<HttpClient>(), prefix));

        services.AddScoped<IWorkflowInboxClient>(sp =>
            new HttpWorkflowInboxClient(sp.GetRequiredService<HttpClient>(), prefix));

        return services;
    }

    /// <summary>
    /// The same, for a host with no <c>HttpClient</c> of its own. See the builder's
    /// equivalent overload for why this is <c>TryAddSingleton</c> rather than scoped.
    /// </summary>
    public static IServiceCollection AddTaskRouterRunnerHttpClient(
        this IServiceCollection services,
        Uri baseAddress,
        string prefix = "/workflow")
    {
        services.TryAddSingleton(_ => new HttpClient { BaseAddress = baseAddress });

        return services.AddTaskRouterRunnerHttpClient(prefix);
    }
}
