using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

using MudBlazor.Services;

using TaskRouter.Blazor;

namespace TaskRouter.Tests;

/// <summary>
/// What the picker shows for the value it was handed — the part somebody reads before they
/// type anything.
/// </summary>
[TestClass]
public class OptionPickerTests
{
    private static readonly KeyValuePair<string, string>[] People =
    [
        new("user-a", "Okafor, Ada"),
        new("user-b", "Smith, John"),
    ];

    [TestMethod]
    public async Task The_current_pick_shows_by_name_not_by_key()
    {
        var html = await RenderAsync("user-b");

        StringAssert.Contains(html, "value=\"Smith, John\"");
    }

    [TestMethod]
    public async Task Somebody_no_longer_in_the_list_shows_by_key_rather_than_as_nobody()
    {
        // A blank here reads as "unassigned", and saving the dialog would then make it so.
        var html = await RenderAsync("user-gone");

        StringAssert.Contains(html, "value=\"user-gone\"");
    }

    [TestMethod]
    public async Task The_whole_list_is_not_rendered_up_front()
    {
        // The defect this replaces: several hundred names in the page before anybody asks.
        var html = await RenderAsync(null);

        Assert.DoesNotContain("Okafor", html);
    }

    private static async Task<string> RenderAsync(string? value)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJsRuntime>();

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<OptionPicker>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(OptionPicker.Options)] = People,
                    [nameof(OptionPicker.Value)] = value,
                    [nameof(OptionPicker.Label)] = "Assign to",
                }));

            return output.ToHtmlString();
        });
    }

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
