using TaskRouter.Blazor;

namespace TaskRouter.Tests;

/// <summary>
/// Finding one person among several hundred by typing part of their name.
/// </summary>
[TestClass]
public class OptionSearchTests
{
    private static readonly KeyValuePair<string, string>[] People =
    [
        new("1", "Smith, John"),
        new("2", "Joanna Smithers"),
        new("3", "Okafor, Ada"),
        new("4", "Johnson, Mary"),
    ];

    private static string Found(string? text) =>
        string.Join(",", OptionSearch.Find(People, text).Select(p => p.Key));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Nothing_typed_offers_everybody_in_the_hosts_order(string? text) =>
        Assert.AreEqual("1,2,3,4", Found(text));

    [TestMethod]
    [DataRow("oka", "3")]
    [DataRow("SMITH", "1,2")]
    [DataRow("john", "1,4")]
    [DataRow("john smith", "1")]
    [DataRow("smith john", "1")]
    [DataRow("jo smi", "1,2")]
    [DataRow("  ada  ", "3")]
    [DataRow("zzz", "")]
    public void Every_word_typed_must_appear_in_the_name_in_any_order_or_case(
        string text, string expected) =>
        Assert.AreEqual(expected, Found(text));
}
