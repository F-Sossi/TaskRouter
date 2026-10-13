using TaskRouter.Blazor;

namespace TaskRouter.Tests;

/// <summary>
/// Turning whatever a host wrote into a URL that stays under the app's base.
///
/// <para>The first host got this wrong twice, in opposite directions, on the same day: its
/// inbox links were root-relative <i>without</i> the sub-path, and its HTTP prefix was
/// root-relative <i>with</i> it. Both are reasonable things to write, so both have to work.</para>
/// </summary>
[TestClass]
public class AppRelativeUrlTests
{
    [TestMethod]
    [DataRow("documents/42", "/app/", "documents/42")]
    [DataRow("/documents/42", "/app/", "documents/42")]
    [DataRow("/app/documents/42", "/app/", "documents/42")]
    [DataRow("/APP/documents/42", "/app/", "documents/42")]
    [DataRow("/app", "/app/", "")]
    [DataRow("/application/42", "/app/", "application/42")]
    [DataRow("/documents/42?groupId=2", "/app/", "documents/42?groupId=2")]
    [DataRow("/documents/42", "/", "documents/42")]
    [DataRow("/documents/42", "/app", "documents/42")]
    [DataRow("/app/documents/42", "/app", "app/documents/42")]
    [DataRow("/documents/42", null, "documents/42")]
    [DataRow("", "/app/", "")]
    public void A_url_inside_the_app_becomes_relative_to_its_base(
        string url, string? basePath, string expected) =>
        Assert.AreEqual(expected, AppRelativeUrl.From(url, basePath));

    [TestMethod]
    [DataRow("https://elsewhere.example/documents/42")]
    [DataRow("//elsewhere.example/documents/42")]
    [DataRow("mailto:someone@example.com")]
    public void A_url_that_leaves_the_app_is_left_alone(string url) =>
        Assert.AreEqual(url, AppRelativeUrl.From(url, "/app/"));
}
